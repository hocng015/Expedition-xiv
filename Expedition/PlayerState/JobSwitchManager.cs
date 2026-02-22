using Lumina.Excel.Sheets;

namespace Expedition.PlayerState;

/// <summary>
/// Manages class/job switching between gatherer and crafter jobs.
///
/// Pain points addressed:
/// - Multi-class dependency chains: recipe A needs BSM sub-craft, B needs WVR, etc.
/// - Switching between gatherer and crafter loses gathering state
/// - Closing crafting log required before switching DoH classes
/// - No buffs carry over between class switches
/// - Gear sets are client-side, must verify correct gear set exists
///
/// Note: Artisan handles DoH class switching for sub-crafts internally.
/// This manager handles the broader gather→craft transitions and
/// pre-flight job readiness validation.
/// </summary>
public sealed class JobSwitchManager
{
    // ClassJob RowIds for relevant jobs
    public const uint CRP = 8;
    public const uint BSM = 9;
    public const uint ARM = 10;
    public const uint GSM = 11;
    public const uint LTW = 12;
    public const uint WVR = 13;
    public const uint ALC = 14;
    public const uint CUL = 15;
    public const uint MIN = 16;
    public const uint BTN = 17;
    public const uint FSH = 18;

    // --- ExpArrayIndex lookup ---
    // PlayerState.ClassJobLevels is indexed by ExpArrayIndex (from the ClassJob Lumina sheet),
    // NOT by ClassJob RowId. We cache these at init time for fast lookups.
    private static readonly Dictionary<uint, int> ExpArrayIndexCache = new();
    private static bool expArrayIndexInitialized;

    /// <summary>
    /// Initializes the ExpArrayIndex cache from the Lumina ClassJob sheet.
    /// Must be called once after DalamudApi is ready.
    /// </summary>
    public static void InitializeExpArrayIndices()
    {
        if (expArrayIndexInitialized) return;

        try
        {
            var classJobSheet = DalamudApi.DataManager.GetExcelSheet<ClassJob>();
            if (classJobSheet == null)
            {
                DalamudApi.Log.Warning("[JobSwitch] ClassJob sheet unavailable — level lookups will be incorrect.");
                return;
            }

            for (uint jobId = CRP; jobId <= FSH; jobId++)
            {
                var row = classJobSheet.GetRow(jobId);
                var expIdx = (int)row.ExpArrayIndex;
                ExpArrayIndexCache[jobId] = expIdx;
                DalamudApi.Log.Debug($"[JobSwitch] ClassJob {jobId} → ExpArrayIndex {expIdx}");
            }

            expArrayIndexInitialized = true;
            DalamudApi.Log.Information(
                $"[JobSwitch] ExpArrayIndex cache initialized: " +
                $"MIN={GetExpArrayIndex(MIN)}, BTN={GetExpArrayIndex(BTN)}, FSH={GetExpArrayIndex(FSH)}");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[JobSwitch] Failed to init ExpArrayIndex cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the ExpArrayIndex for a ClassJob RowId.
    /// This is needed because PlayerState.ClassJobLevels is indexed by ExpArrayIndex, not RowId.
    /// Returns -1 if the index is not cached.
    /// </summary>
    public static int GetExpArrayIndex(uint classJobRowId)
    {
        if (ExpArrayIndexCache.TryGetValue(classJobRowId, out var idx))
            return idx;
        return -1;
    }

    /// <summary>
    /// Reads a single ClassJob level from PlayerState using the correct ExpArrayIndex.
    /// Returns -1 if the level cannot be read.
    /// </summary>
    public static unsafe int GetPlayerJobLevel(uint classJobRowId)
    {
        var expIdx = GetExpArrayIndex(classJobRowId);
        if (expIdx < 0) return -1;

        try
        {
            var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (playerState == null) return -1;
            return playerState->ClassJobLevels[expIdx];
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Reads a single ClassJob's current XP (within the level) from PlayerState.
    /// Uses the same ExpArrayIndex mapping as GetPlayerJobLevel.
    /// Returns -1 if the XP cannot be read.
    /// </summary>
    public static unsafe int GetPlayerJobExperience(uint classJobRowId)
    {
        var expIdx = GetExpArrayIndex(classJobRowId);
        if (expIdx < 0) return -1;

        try
        {
            var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (playerState == null) return -1;
            return playerState->ClassJobExperience[expIdx];
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Returns the total XP required to advance from the given level to the next level.
    /// Uses the ParamGrow Lumina sheet. Returns 0 if the level is max or data unavailable.
    /// </summary>
    public static int GetXpToNextLevel(int currentLevel)
    {
        if (currentLevel <= 0) return 0;

        try
        {
            var sheet = DalamudApi.DataManager.GetExcelSheet<ParamGrow>();
            if (sheet == null) return 0;
            var row = sheet.GetRow((uint)currentLevel);
            return (int)row.ExpToNext;
        }
        catch
        {
            return 0;
        }
    }

    private static readonly Dictionary<int, uint> CraftTypeToClassJob = new()
    {
        { 0, CRP }, { 1, BSM }, { 2, ARM }, { 3, GSM },
        { 4, LTW }, { 5, WVR }, { 6, ALC }, { 7, CUL },
    };

    /// <summary>
    /// Returns the ClassJob ID for a CraftType index.
    /// </summary>
    public static uint CraftTypeToJobId(int craftTypeId)
        => CraftTypeToClassJob.GetValueOrDefault(craftTypeId, 0u);

    /// <summary>
    /// Gets the current player ClassJob ID.
    /// </summary>
    public static uint GetCurrentJobId()
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null) return 0;
        return player.ClassJob.RowId;
    }

    /// <summary>
    /// Returns true if the player is currently on a gathering class.
    /// </summary>
    public static bool IsOnGatherer()
    {
        var job = GetCurrentJobId();
        return job == MIN || job == BTN || job == FSH;
    }

    /// <summary>
    /// Returns true if the player is currently on a crafting class.
    /// </summary>
    public static bool IsOnCrafter()
    {
        var job = GetCurrentJobId();
        return job >= CRP && job <= CUL;
    }

    /// <summary>
    /// Validates that all crafting classes needed by the resolved recipe
    /// are at sufficient level. Returns a list of problems found.
    /// </summary>
    public static unsafe List<string> ValidateCraftingLevels(
        IEnumerable<RecipeResolver.CraftStep> craftOrder)
    {
        var problems = new List<string>();
        var playerLevels = GetPlayerClassLevels();

        foreach (var step in craftOrder)
        {
            var requiredJob = CraftTypeToJobId(step.Recipe.CraftTypeId);
            var jobName = RecipeResolver.RecipeResolverService.GetCraftTypeName(step.Recipe.CraftTypeId);
            var requiredLevel = step.Recipe.RequiredLevel;

            if (requiredJob == 0) continue;

            if (playerLevels.TryGetValue(requiredJob, out var actualLevel))
            {
                if (actualLevel < requiredLevel)
                {
                    problems.Add($"{jobName} is Lv{actualLevel} but Lv{requiredLevel} is needed for {step.Recipe.ItemName}");
                }
            }
            else
            {
                // Could not determine level — warn
                problems.Add($"Could not verify {jobName} level for {step.Recipe.ItemName} (requires Lv{requiredLevel})");
            }
        }

        return problems;
    }

    /// <summary>
    /// Reads actual player class/job levels from the game's PlayerState struct.
    /// Returns a dictionary of ClassJob RowId -> level.
    ///
    /// IMPORTANT: PlayerState.ClassJobLevels is indexed by ExpArrayIndex (from the
    /// ClassJob Lumina sheet), NOT by ClassJob RowId. We use the cached ExpArrayIndex
    /// lookup to translate RowId → array index.
    /// </summary>
    private static unsafe Dictionary<uint, int> GetPlayerClassLevels()
    {
        var levels = new Dictionary<uint, int>();

        try
        {
            var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (playerState == null) return levels;

            // ClassJob RowIds 8-18 map to DoH/DoL (CRP=8 through FSH=18)
            // Must translate RowId → ExpArrayIndex before indexing ClassJobLevels
            for (uint jobId = CRP; jobId <= FSH; jobId++)
            {
                var expIdx = GetExpArrayIndex(jobId);
                if (expIdx < 0)
                {
                    DalamudApi.Log.Debug($"[JobSwitch] No ExpArrayIndex for ClassJob {jobId} — skipping.");
                    continue;
                }
                var level = playerState->ClassJobLevels[expIdx];
                levels[jobId] = level;
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Debug($"Failed to read player levels: {ex.Message}");
        }

        return levels;
    }

    /// <summary>
    /// Determines the set of unique gathering classes needed for the gather list.
    /// </summary>
    public static HashSet<RecipeResolver.GatherType> GetRequiredGatherClasses(
        IEnumerable<RecipeResolver.MaterialRequirement> gatherList)
    {
        var required = new HashSet<RecipeResolver.GatherType>();
        foreach (var mat in gatherList)
        {
            if (mat.GatherType != RecipeResolver.GatherType.None)
                required.Add(mat.GatherType);
        }
        return required;
    }

    /// <summary>
    /// Determines the set of unique crafting classes needed for all craft steps.
    /// </summary>
    public static HashSet<int> GetRequiredCraftClasses(
        IEnumerable<RecipeResolver.CraftStep> craftOrder)
    {
        var required = new HashSet<int>();
        foreach (var step in craftOrder)
        {
            required.Add(step.Recipe.CraftTypeId);
        }
        return required;
    }
}
