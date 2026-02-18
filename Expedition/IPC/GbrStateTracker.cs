using System.Reflection;

namespace Expedition.IPC;

/// <summary>
/// Reflects into GatherBuddy Reborn's AutoGather subsystem to extract
/// structured disable reasons. Uses the same reflection approach as
/// <see cref="GatherBuddyListManager"/> for list management.
///
/// When GBR's AutoGather disables itself, it sets an internal <c>AutoStatus</c>
/// string explaining why. This class reads that string plus additional internal
/// state (TaskManager queue, active item list, amiss counter) to classify the
/// disable reason into a <see cref="GbrDisableReason"/> enum.
///
/// All reflection is best-effort — if any field can't be found, the tracker
/// returns <see cref="GbrDisableReason.Unknown"/> and logs a warning.
/// </summary>
public sealed class GbrStateTracker
{
    private const BindingFlags AllFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    // Cached reflection handles — read
    private object? autoGatherObj;
    private PropertyInfo? autoStatusProp;
    private FieldInfo? autoStatusField;
    private PropertyInfo? taskManagerProp;
    private PropertyInfo? taskManagerNumQueuedProp;
    private PropertyInfo? taskManagerIsBusyProp;
    private object? activeItemList;
    private PropertyInfo? hasItemsToGatherProp;
    private FieldInfo? consecutiveAmissField;
    private FieldInfo? currentGatherTargetField;

    // Cached reflection handles — write (for ForceReset)
    private MethodInfo? taskManagerAbortMethod;
    private FieldInfo? stuckAtSpotField;
    private FieldInfo? jiggleAttemptsField;
    private FieldInfo? unstuckStartField;

    public bool IsInitialized { get; private set; }

    /// <summary>
    /// Initializes reflection handles by probing GBR's AutoGather internals.
    /// Must be called after <see cref="GatherBuddyListManager.Initialize()"/>
    /// so that the GBR plugin instance is available.
    /// </summary>
    public bool Initialize(object? gbrPluginInstance)
    {
        if (gbrPluginInstance == null)
        {
            DalamudApi.Log.Warning("[GbrStateTracker] No GBR plugin instance — state tracking disabled.");
            return false;
        }

        try
        {
            var gbrType = gbrPluginInstance.GetType();

            // AutoGather is a STATIC property on the GatherBuddy class (not on the plugin instance).
            // GBR's code: public static AutoGather AutoGather { get; private set; }
            // We need to find the GatherBuddy class in the same assembly and read the static property.
            autoGatherObj = GetMemberValue(gbrType, gbrPluginInstance, "AutoGather");
            if (autoGatherObj == null)
            {
                // Try finding it as a static property on a "GatherBuddy" class in the same assembly
                var gbrAssembly = gbrType.Assembly;
                var gatherBuddyClass = gbrAssembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "GatherBuddy" && !t.IsInterface);
                if (gatherBuddyClass != null)
                {
                    var autoGatherProp = gatherBuddyClass.GetProperty("AutoGather",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (autoGatherProp != null)
                    {
                        autoGatherObj = autoGatherProp.GetValue(null);
                        DalamudApi.Log.Information(
                            $"[GbrStateTracker] Found AutoGather via static property on {gatherBuddyClass.FullName}");
                    }
                }
            }

            if (autoGatherObj == null)
            {
                DalamudApi.Log.Warning("[GbrStateTracker] Could not find AutoGather on GBR plugin or GatherBuddy class. " +
                    "State tracking disabled.");
                LogAvailableMembers(gbrType, "GBR plugin");
                return false;
            }

            var agType = autoGatherObj.GetType();
            DalamudApi.Log.Debug($"[GbrStateTracker] Found AutoGather: {agType.FullName}");

            // AutoStatus — try property first, then backing field
            autoStatusProp = agType.GetProperty("AutoStatus", AllFlags);
            if (autoStatusProp == null)
                autoStatusField = FindField(agType, "_autoStatus", "autoStatus", "AutoStatus");

            // TaskManager
            taskManagerProp = agType.GetProperty("TaskManager", AllFlags);
            if (taskManagerProp != null)
            {
                var tmObj = taskManagerProp.GetValue(autoGatherObj);
                if (tmObj != null)
                {
                    var tmType = tmObj.GetType();
                    taskManagerNumQueuedProp = tmType.GetProperty("NumQueuedTasks", AllFlags);
                    taskManagerIsBusyProp = tmType.GetProperty("IsBusy", AllFlags);
                    taskManagerAbortMethod = tmType.GetMethod("Abort", AllFlags)
                        ?? tmType.GetMethod("AbortAllTasks", AllFlags)
                        ?? tmType.GetMethod("AbortAll", AllFlags);
                    DalamudApi.Log.Debug(
                        $"[GbrStateTracker] Found TaskManager: NumQueuedTasks={taskManagerNumQueuedProp != null}, " +
                        $"IsBusy={taskManagerIsBusyProp != null}, Abort={taskManagerAbortMethod != null}");
                }
            }

            // _activeItemList (field) or ActiveItemList (property)
            activeItemList = GetMemberValue(agType, autoGatherObj, "_activeItemList", "ActiveItemList");
            if (activeItemList != null)
            {
                hasItemsToGatherProp = activeItemList.GetType().GetProperty("HasItemsToGather", AllFlags);
                DalamudApi.Log.Debug($"[GbrStateTracker] Found ActiveItemList: HasItemsToGather={hasItemsToGatherProp != null}");
            }

            // _consecutiveAmissCount
            consecutiveAmissField = FindField(agType, "_consecutiveAmissCount", "consecutiveAmissCount");

            // _currentGatherTarget
            currentGatherTargetField = FindField(agType, "_currentGatherTarget", "currentGatherTarget");

            // Write-capable fields for ForceReset
            stuckAtSpotField = FindField(agType, "_stuckAtSpotStartTime", "stuckAtSpotStartTime");
            jiggleAttemptsField = FindField(agType, "_jiggleAttempts", "jiggleAttempts");
            unstuckStartField = FindField(agType, "unstuckStart", "_unstuckStart");

            IsInitialized = true;

            var capabilities = new List<string>();
            if (autoStatusProp != null || autoStatusField != null) capabilities.Add("AutoStatus");
            if (taskManagerProp != null) capabilities.Add("TaskManager");
            if (taskManagerAbortMethod != null) capabilities.Add("TaskAbort");
            if (hasItemsToGatherProp != null) capabilities.Add("HasItemsToGather");
            if (consecutiveAmissField != null) capabilities.Add("AmissCount");
            if (currentGatherTargetField != null) capabilities.Add("GatherTarget");
            if (stuckAtSpotField != null) capabilities.Add("StuckReset");
            if (jiggleAttemptsField != null) capabilities.Add("JiggleReset");

            DalamudApi.Log.Information(
                $"[GbrStateTracker] Initialized. Capabilities: [{string.Join(", ", capabilities)}]");
            return true;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[GbrStateTracker] Initialization failed.");
            return false;
        }
    }

    /// <summary>
    /// Takes a snapshot of GBR's AutoGather internal state and classifies the disable reason.
    /// Safe to call at any time — returns a default snapshot if not initialized.
    /// </summary>
    public GbrDisableSnapshot GetSnapshot()
    {
        if (!IsInitialized || autoGatherObj == null)
        {
            return new GbrDisableSnapshot
            {
                AutoStatus = string.Empty,
                Reason = GbrDisableReason.Unknown,
                Timestamp = DateTime.Now,
            };
        }

        try
        {
            var autoStatus = ReadAutoStatus();
            var taskQueueCount = ReadTaskQueueCount();
            var taskManagerBusy = ReadTaskManagerBusy();
            var hasItems = ReadHasItemsToGather();
            var amissCount = ReadConsecutiveAmissCount();
            var hasTarget = ReadHasGatherTarget();

            var reason = ClassifyReason(autoStatus, taskQueueCount, taskManagerBusy, hasItems, amissCount, hasTarget);

            return new GbrDisableSnapshot
            {
                AutoStatus = autoStatus,
                TaskQueueCount = taskQueueCount,
                TaskManagerBusy = taskManagerBusy,
                HasItemsToGather = hasItems,
                ConsecutiveAmissCount = amissCount,
                HasGatherTarget = hasTarget,
                Reason = reason,
                Timestamp = DateTime.Now,
            };
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[GbrStateTracker] Failed to read snapshot.");
            return new GbrDisableSnapshot
            {
                AutoStatus = string.Empty,
                Reason = GbrDisableReason.Unknown,
                Timestamp = DateTime.Now,
            };
        }
    }

    /// <summary>
    /// Force-resets GBR's AutoGather internal state via reflection.
    /// This is equivalent to a "soft restart" of GBR's AutoGather subsystem — it clears
    /// stale task queues, amiss counters, stuck timers, and jiggle attempts without
    /// requiring a full game restart.
    ///
    /// Call this when GBR's AutoGather gets into a corrupted state that can't be
    /// recovered through normal IPC (disable/re-enable/re-inject).
    /// </summary>
    /// <param name="gbrIpc">GBR IPC handle to disable/re-enable AutoGather cleanly.</param>
    /// <returns>True if the reset was at least partially successful.</returns>
    public bool ForceReset(GatherBuddyIpc gbrIpc)
    {
        if (!IsInitialized || autoGatherObj == null)
        {
            DalamudApi.Log.Warning("[GbrStateTracker:Reset] Not initialized — cannot reset.");
            return false;
        }

        DalamudApi.Log.Information("[GbrStateTracker:Reset] Starting GBR AutoGather force-reset...");
        var resetCount = 0;

        try
        {
            // Step 1: Disable AutoGather cleanly via IPC first
            gbrIpc.SetAutoGatherEnabled(false);
            DalamudApi.Log.Debug("[GbrStateTracker:Reset] AutoGather disabled via IPC.");

            // Step 2: Abort all queued tasks in GBR's TaskManager
            if (taskManagerProp != null && taskManagerAbortMethod != null)
            {
                try
                {
                    var tm = taskManagerProp.GetValue(autoGatherObj);
                    if (tm != null)
                    {
                        taskManagerAbortMethod.Invoke(tm, null);
                        resetCount++;
                        DalamudApi.Log.Debug("[GbrStateTracker:Reset] TaskManager.Abort() called.");
                    }
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrStateTracker:Reset] TaskManager abort failed: {ex.Message}");
                }
            }

            // Step 3: Reset _consecutiveAmissCount to 0
            if (consecutiveAmissField != null)
            {
                try
                {
                    consecutiveAmissField.SetValue(autoGatherObj, 0);
                    resetCount++;
                    DalamudApi.Log.Debug("[GbrStateTracker:Reset] _consecutiveAmissCount → 0");
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrStateTracker:Reset] AmissCount reset failed: {ex.Message}");
                }
            }

            // Step 4: Clear _currentGatherTarget to null
            if (currentGatherTargetField != null)
            {
                try
                {
                    currentGatherTargetField.SetValue(autoGatherObj, null);
                    resetCount++;
                    DalamudApi.Log.Debug("[GbrStateTracker:Reset] _currentGatherTarget → null");
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrStateTracker:Reset] GatherTarget reset failed: {ex.Message}");
                }
            }

            // Step 5: Clear AutoStatus string
            if (autoStatusProp is { CanWrite: true })
            {
                try
                {
                    autoStatusProp.SetValue(autoGatherObj, string.Empty);
                    resetCount++;
                    DalamudApi.Log.Debug("[GbrStateTracker:Reset] AutoStatus → empty");
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrStateTracker:Reset] AutoStatus reset failed: {ex.Message}");
                }
            }
            else if (autoStatusField != null)
            {
                try
                {
                    autoStatusField.SetValue(autoGatherObj, string.Empty);
                    resetCount++;
                    DalamudApi.Log.Debug("[GbrStateTracker:Reset] AutoStatus field → empty");
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrStateTracker:Reset] AutoStatus field reset failed: {ex.Message}");
                }
            }

            // Step 6: Reset stuck detection timers
            if (stuckAtSpotField != null)
            {
                try
                {
                    stuckAtSpotField.SetValue(autoGatherObj, DateTime.MinValue);
                    resetCount++;
                    DalamudApi.Log.Debug("[GbrStateTracker:Reset] _stuckAtSpotStartTime → MinValue");
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrStateTracker:Reset] StuckAtSpot reset failed: {ex.Message}");
                }
            }

            if (unstuckStartField != null)
            {
                try
                {
                    // unstuckStart is DateTime? (nullable)
                    unstuckStartField.SetValue(autoGatherObj, null);
                    resetCount++;
                    DalamudApi.Log.Debug("[GbrStateTracker:Reset] unstuckStart → null");
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrStateTracker:Reset] UnstuckStart reset failed: {ex.Message}");
                }
            }

            // Step 7: Clear jiggle attempts dictionary
            if (jiggleAttemptsField != null)
            {
                try
                {
                    var jiggleDict = jiggleAttemptsField.GetValue(autoGatherObj);
                    if (jiggleDict != null)
                    {
                        var clearMethod = jiggleDict.GetType().GetMethod("Clear", AllFlags);
                        clearMethod?.Invoke(jiggleDict, null);
                        resetCount++;
                        DalamudApi.Log.Debug("[GbrStateTracker:Reset] _jiggleAttempts → cleared");
                    }
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrStateTracker:Reset] JiggleAttempts clear failed: {ex.Message}");
                }
            }

            DalamudApi.Log.Information(
                $"[GbrStateTracker:Reset] Force-reset complete. {resetCount} fields reset successfully.");
            return resetCount > 0;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[GbrStateTracker:Reset] Force-reset failed.");
            return false;
        }
    }

    // --- Field readers (all null-safe, return defaults on failure) ---

    private string ReadAutoStatus()
    {
        try
        {
            if (autoStatusProp != null)
                return autoStatusProp.GetValue(autoGatherObj) as string ?? string.Empty;
            if (autoStatusField != null)
                return autoStatusField.GetValue(autoGatherObj) as string ?? string.Empty;
        }
        catch { /* reflection may throw */ }
        return string.Empty;
    }

    private int ReadTaskQueueCount()
    {
        try
        {
            if (taskManagerProp == null || taskManagerNumQueuedProp == null) return -1;
            var tm = taskManagerProp.GetValue(autoGatherObj);
            if (tm == null) return -1;
            return taskManagerNumQueuedProp.GetValue(tm) as int? ?? -1;
        }
        catch { return -1; }
    }

    private bool ReadTaskManagerBusy()
    {
        try
        {
            if (taskManagerProp == null || taskManagerIsBusyProp == null) return false;
            var tm = taskManagerProp.GetValue(autoGatherObj);
            if (tm == null) return false;
            return taskManagerIsBusyProp.GetValue(tm) as bool? ?? false;
        }
        catch { return false; }
    }

    private bool ReadHasItemsToGather()
    {
        try
        {
            if (activeItemList == null || hasItemsToGatherProp == null) return true; // Assume true if unknown
            return hasItemsToGatherProp.GetValue(activeItemList) as bool? ?? true;
        }
        catch { return true; }
    }

    private int ReadConsecutiveAmissCount()
    {
        try
        {
            if (consecutiveAmissField == null) return 0;
            return consecutiveAmissField.GetValue(autoGatherObj) as int? ?? 0;
        }
        catch { return 0; }
    }

    private bool ReadHasGatherTarget()
    {
        try
        {
            if (currentGatherTargetField == null) return false;
            return currentGatherTargetField.GetValue(autoGatherObj) != null;
        }
        catch { return false; }
    }

    // --- Classification ---

    private static GbrDisableReason ClassifyReason(
        string autoStatus, int taskQueueCount, bool taskManagerBusy,
        bool hasItems, int amissCount, bool hasTarget)
    {
        // Priority 1: Parse AutoStatus string for known patterns
        if (!string.IsNullOrEmpty(autoStatus))
        {
            var s = autoStatus;

            if (s.Contains("user disabled", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Stopping collectable turn-in (user disabled", StringComparison.OrdinalIgnoreCase))
                return GbrDisableReason.UserDisabled;

            if (s.Contains("navmesh", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Could not find valid adjustment position", StringComparison.OrdinalIgnoreCase))
                return GbrDisableReason.NavmeshFailure;

            if (s.Contains("Inventory full", StringComparison.OrdinalIgnoreCase))
                return GbrDisableReason.InventoryFull;

            if (s.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                || s.Contains("not enabled", StringComparison.OrdinalIgnoreCase))
                return GbrDisableReason.MissingPlugin;

            if (s.Contains("Cannot add", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Cannot enable", StringComparison.OrdinalIgnoreCase))
                return GbrDisableReason.NoValidTargets;

            if (s.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || s.Contains("timer expired", StringComparison.OrdinalIgnoreCase))
                return GbrDisableReason.Timeout;

            if (s.Contains("Teleport home", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Lifestream", StringComparison.OrdinalIgnoreCase))
                return GbrDisableReason.TeleportFailed;

            if (s.Contains("quest has not been completed", StringComparison.OrdinalIgnoreCase))
                return GbrDisableReason.QuestIncomplete;
        }

        // Priority 2: Structural analysis when AutoStatus is empty
        if (!hasItems && !hasTarget)
            return GbrDisableReason.ListExhausted;

        if (amissCount > 3)
            return GbrDisableReason.AmissAtNode;

        if (taskQueueCount == 0 && !taskManagerBusy && hasItems)
            return GbrDisableReason.InternalError;

        return GbrDisableReason.Unknown;
    }

    /// <summary>
    /// Runs a deep diagnostic on GBR's ActiveItemList internals to determine
    /// why UpdateItemsToGather() produces an empty _gatherableItems list.
    /// Call this when HasItemsToGather=false despite correct _activeItems.
    /// </summary>
    public void DiagnoseEmptyGatherableItems()
    {
        if (!IsInitialized || autoGatherObj == null || activeItemList == null)
        {
            DalamudApi.Log.Warning("[GbrDiag] Cannot diagnose: GbrStateTracker not initialized.");
            return;
        }

        try
        {
            var ailType = activeItemList.GetType();

            // 1. Read _gatherableItems.Count
            var gatherableItemsField = ailType.GetField("_gatherableItems", AllFlags);
            if (gatherableItemsField != null)
            {
                var gatherableItems = gatherableItemsField.GetValue(activeItemList);
                if (gatherableItems is System.Collections.ICollection col)
                    DalamudApi.Log.Information($"[GbrDiag] _gatherableItems.Count = {col.Count}");
                else
                    DalamudApi.Log.Warning($"[GbrDiag] _gatherableItems is not ICollection: {gatherableItems?.GetType().Name ?? "null"}");
            }
            else
            {
                DalamudApi.Log.Warning("[GbrDiag] Could not find _gatherableItems field on ActiveItemList");
            }

            // 2. Read _lastUpdateTime to see if DoUpdate has run
            var lastUpdateField = ailType.GetField("_lastUpdateTime", AllFlags);
            if (lastUpdateField != null)
            {
                var lastUpdate = lastUpdateField.GetValue(activeItemList);
                // TimeStamp.ToString() crashes on MinValue, so use the raw numeric value
                var rawValue = "?";
                try
                {
                    // TimeStamp is a struct wrapping a long
                    var valueField = lastUpdate?.GetType().GetField("Value", AllFlags)
                        ?? lastUpdate?.GetType().GetField("value", AllFlags);
                    if (valueField != null)
                        rawValue = valueField.GetValue(lastUpdate)?.ToString() ?? "null";
                    else
                        rawValue = lastUpdate?.GetType().GetFields(AllFlags)
                            .FirstOrDefault()?.GetValue(lastUpdate)?.ToString() ?? "opaque";
                }
                catch { rawValue = "error"; }
                var isMinValue = rawValue == long.MinValue.ToString() || rawValue == "-9223372036854775808";
                DalamudApi.Log.Information(
                    $"[GbrDiag] _lastUpdateTime raw={rawValue} " +
                    (isMinValue ? "(MinValue — DoUpdate has NOT run yet!)" : "(DoUpdate has run)"));
            }

            // 3. Read _activeItemsChanged flag
            var aiChangedField = ailType.GetField("_activeItemsChanged", AllFlags);
            if (aiChangedField != null)
            {
                var changed = aiChangedField.GetValue(activeItemList);
                DalamudApi.Log.Information($"[GbrDiag] _activeItemsChanged = {changed}");
            }

            // 4. Get _listsManager from ActiveItemList
            var lmField = ailType.GetField("_listsManager", AllFlags);
            var listsManager = lmField?.GetValue(activeItemList);
            if (listsManager == null)
            {
                DalamudApi.Log.Warning("[GbrDiag] Could not get _listsManager from ActiveItemList");
                return;
            }

            // 5. Read ActiveItems from listsManager
            var activeItemsProp = listsManager.GetType().GetProperty("ActiveItems");
            var activeItems = activeItemsProp?.GetValue(listsManager);
            if (activeItems is not System.Collections.IEnumerable aiEnum)
            {
                DalamudApi.Log.Warning("[GbrDiag] ActiveItems is not enumerable");
                return;
            }

            var itemIndex = 0;
            foreach (var ai in aiEnum)
            {
                var aiType2 = ai.GetType();
                var item = aiType2.GetField("Item1")?.GetValue(ai)
                        ?? aiType2.GetProperty("Item")?.GetValue(ai);
                var qty = aiType2.GetField("Item2")?.GetValue(ai)
                       ?? aiType2.GetProperty("Quantity")?.GetValue(ai);

                if (item == null)
                {
                    DalamudApi.Log.Warning($"[GbrDiag] ActiveItem[{itemIndex}]: item is null!");
                    itemIndex++;
                    continue;
                }

                var itemType = item.GetType();
                var itemId = itemType.GetProperty("ItemId")?.GetValue(item);
                var itemName = itemType.GetProperty("Name")?.GetValue(item);

                // GetTotalCount
                // GetTotalCount is an extension method, not directly on the type
                int totalCount = -1;
                // GetTotalCount is an extension method in GatherableExtensions, try calling it
                try
                {
                    var extType = item.GetType().Assembly.GetTypes()
                        .FirstOrDefault(t => t.Name == "GatherableExtensions");
                    if (extType != null)
                    {
                        var gtcMethod = extType.GetMethod("GetTotalCount",
                            BindingFlags.Public | BindingFlags.Static);
                        if (gtcMethod != null)
                        {
                            var result = gtcMethod.Invoke(null, new[] { item });
                            totalCount = result is int tc ? tc : -1;
                        }
                    }
                }
                catch (Exception ex)
                {
                    DalamudApi.Log.Debug($"[GbrDiag] GetTotalCount invocation failed: {ex.Message}");
                }

                // Locations
                var locationsProp = itemType.GetProperty("Locations");
                var locations = locationsProp?.GetValue(item);
                var locCount = -1;
                if (locations is System.Collections.ICollection locCol)
                    locCount = locCol.Count;
                else if (locations is System.Collections.IEnumerable locEnum)
                {
                    locCount = 0;
                    foreach (var _ in locEnum) locCount++;
                }

                DalamudApi.Log.Information(
                    $"[GbrDiag] ActiveItem[{itemIndex}]: id={itemId}, name={itemName}, " +
                    $"qty={qty}, totalCount={totalCount}, locations={locCount}");

                // Log each location's details (level, type)
                if (locations is System.Collections.IEnumerable locEnum2)
                {
                    var locIdx = 0;
                    foreach (var loc in locEnum2)
                    {
                        var locType = loc.GetType();
                        var locName = locType.GetProperty("Name")?.GetValue(loc);
                        var locLevel = locType.GetProperty("Level")?.GetValue(loc);
                        var locGatherType = locType.GetProperty("GatheringType")?.GetValue(loc);
                        var locNodeType = locType.GetProperty("NodeType")?.GetValue(loc);
                        var locTerritory = locType.GetProperty("Territory")?.GetValue(loc);
                        var territoryId = locTerritory?.GetType().GetProperty("Id")?.GetValue(locTerritory);

                        DalamudApi.Log.Information(
                            $"[GbrDiag]   Location[{locIdx}]: name={locName}, level={locLevel}, " +
                            $"gatherType={locGatherType}, nodeType={locNodeType}, " +
                            $"territory={territoryId}, type={locType.Name}");
                        locIdx++;
                        if (locIdx > 5) { DalamudApi.Log.Debug($"[GbrDiag]   ... and more locations"); break; }
                    }
                }

                itemIndex++;
            }

            // 6. Read DiscipleOfLand levels
            try
            {
                var gbrAssembly = autoGatherObj.GetType().Assembly;
                var dolType = gbrAssembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "DiscipleOfLand");
                if (dolType != null)
                {
                    var minerLevelProp = dolType.GetProperty("MinerLevel", BindingFlags.Public | BindingFlags.Static);
                    var botanistLevelProp = dolType.GetProperty("BotanistLevel", BindingFlags.Public | BindingFlags.Static);
                    var perceptionProp = dolType.GetProperty("Perception", BindingFlags.Public | BindingFlags.Static);
                    var minerLevel = minerLevelProp?.GetValue(null);
                    var botanistLevel = botanistLevelProp?.GetValue(null);
                    var perception = perceptionProp?.GetValue(null);

                    // GBR calculates: (level + 5) / 5 * 5 for the filter threshold
                    int minerThreshold = minerLevel is int ml ? (ml + 5) / 5 * 5 : -1;
                    int botanistThreshold = botanistLevel is int bl ? (bl + 5) / 5 * 5 : -1;

                    DalamudApi.Log.Information(
                        $"[GbrDiag] DiscipleOfLand: MinerLevel={minerLevel} (threshold={minerThreshold}), " +
                        $"BotanistLevel={botanistLevel} (threshold={botanistThreshold}), " +
                        $"Perception={perception}");
                }
            }
            catch (Exception ex)
            {
                DalamudApi.Log.Debug($"[GbrDiag] DiscipleOfLand probe failed: {ex.Message}");
            }

            // 7. Read Player.Job
            try
            {
                var gbrAssembly = autoGatherObj.GetType().Assembly;
                var playerType = gbrAssembly.GetTypes()
                    .FirstOrDefault(t => t.Name == "Player" && t.GetProperty("Job", BindingFlags.Public | BindingFlags.Static) != null);
                if (playerType != null)
                {
                    var jobProp = playerType.GetProperty("Job", BindingFlags.Public | BindingFlags.Static);
                    var job = jobProp?.GetValue(null);
                    DalamudApi.Log.Information($"[GbrDiag] Player.Job = {job} (16=Miner, 17=Botanist, 18=Fisher)");
                }
            }
            catch (Exception ex)
            {
                DalamudApi.Log.Debug($"[GbrDiag] Player.Job probe failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[GbrDiag] Diagnosis failed.");
        }
    }

    // --- Reflection helpers ---

    /// <summary>
    /// Gets the value of a field or property by trying multiple names.
    /// Walks the inheritance chain.
    /// </summary>
    private static object? GetMemberValue(Type type, object instance, params string[] names)
    {
        foreach (var name in names)
        {
            var currentType = type;
            while (currentType != null)
            {
                var prop = currentType.GetProperty(name, AllFlags);
                if (prop is { CanRead: true })
                {
                    try
                    {
                        var val = prop.GetValue(prop.GetMethod!.IsStatic ? null : instance);
                        if (val != null) return val;
                    }
                    catch { /* may throw */ }
                }

                var field = currentType.GetField(name, AllFlags);
                if (field != null)
                {
                    try
                    {
                        var val = field.GetValue(field.IsStatic ? null : instance);
                        if (val != null) return val;
                    }
                    catch { /* may throw */ }
                }

                currentType = currentType.BaseType;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds a FieldInfo by trying multiple names on a type and its base types.
    /// </summary>
    private static FieldInfo? FindField(Type type, params string[] names)
    {
        foreach (var name in names)
        {
            var currentType = type;
            while (currentType != null)
            {
                var field = currentType.GetField(name, AllFlags);
                if (field != null) return field;
                currentType = currentType.BaseType;
            }
        }

        return null;
    }

    /// <summary>
    /// Logs available fields and properties on a type for debugging.
    /// </summary>
    private static void LogAvailableMembers(Type type, string label)
    {
        var fields = new List<string>();
        var props = new List<string>();
        var currentType = type;
        while (currentType != null)
        {
            foreach (var f in currentType.GetFields(AllFlags))
                fields.Add($"{f.Name}({f.FieldType.Name})");
            foreach (var p in currentType.GetProperties(AllFlags))
                props.Add($"{p.Name}({p.PropertyType.Name})");
            currentType = currentType.BaseType;
        }

        DalamudApi.Log.Warning(
            $"[GbrStateTracker] Members on '{label}':\n" +
            $"  Fields: {string.Join(", ", fields)}\n" +
            $"  Props: {string.Join(", ", props)}");
    }

    // --- Nested types ---

    /// <summary>
    /// Immutable snapshot of GBR's AutoGather state at a point in time.
    /// </summary>
    public sealed class GbrDisableSnapshot
    {
        /// <summary>Raw AutoStatus string from GBR (set by AbortAutoGather).</summary>
        public string AutoStatus { get; init; } = string.Empty;

        /// <summary>Number of queued tasks in GBR's TaskManager. -1 if unavailable.</summary>
        public int TaskQueueCount { get; init; }

        /// <summary>Whether GBR's TaskManager is currently executing.</summary>
        public bool TaskManagerBusy { get; init; }

        /// <summary>Whether GBR's active item list still has items to gather.</summary>
        public bool HasItemsToGather { get; init; }

        /// <summary>How many consecutive "amiss" results at the current node.</summary>
        public int ConsecutiveAmissCount { get; init; }

        /// <summary>Whether GBR has a current gather target set.</summary>
        public bool HasGatherTarget { get; init; }

        /// <summary>Classified disable reason.</summary>
        public GbrDisableReason Reason { get; init; } = GbrDisableReason.Unknown;

        /// <summary>When this snapshot was taken.</summary>
        public DateTime Timestamp { get; init; }

        /// <summary>One-line summary for logging.</summary>
        public override string ToString()
            => $"reason={Reason}, autoStatus=\"{Truncate(AutoStatus, 80)}\", " +
               $"tasks={TaskQueueCount}, busy={TaskManagerBusy}, " +
               $"hasItems={HasItemsToGather}, amiss={ConsecutiveAmissCount}, " +
               $"hasTarget={HasGatherTarget}";

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s[..max] + "…";
    }
}

/// <summary>
/// Classified reason why GBR's AutoGather disabled itself.
/// </summary>
public enum GbrDisableReason
{
    /// <summary>Could not determine the reason (reflection failed or no signal).</summary>
    Unknown,

    /// <summary>GBR's item list is exhausted — no more items to gather.</summary>
    ListExhausted,

    /// <summary>Navmesh/pathfinding failure — could not find a valid position.</summary>
    NavmeshFailure,

    /// <summary>Player inventory is full.</summary>
    InventoryFull,

    /// <summary>No valid targets matching criteria (wrong list, no locations, etc.).</summary>
    NoValidTargets,

    /// <summary>Repeatedly getting "amiss" at a node (wrong class, empty node, etc.).</summary>
    AmissAtNode,

    /// <summary>GBR's internal timeout triggered.</summary>
    Timeout,

    /// <summary>User manually disabled AutoGather.</summary>
    UserDisabled,

    /// <summary>Required plugin (AutoHook, Lifestream, etc.) is not installed.</summary>
    MissingPlugin,

    /// <summary>Teleport home failed (Lifestream not available).</summary>
    TeleportFailed,

    /// <summary>Required quest not completed (e.g., aetherial reduction).</summary>
    QuestIncomplete,

    /// <summary>TaskManager empty but items remain — likely internal GBR error.</summary>
    InternalError,
}
