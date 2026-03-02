using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

using InventoryManager_Game = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;

namespace Expedition.PlayerState;

/// <summary>
/// Auto-detects and uses the best available food and potions/medicine for the player's
/// current job type (DoH vs DoL) during Cosmic Exploration sessions.
///
/// Scans the Lumina Item + ItemFood sheets at init to classify all consumables into
/// gatherer/crafter food and pots, sorted by item level. At runtime, finds the highest
/// ilvl consumable in inventory (preferring HQ) and uses it when the relevant buff is
/// missing or about to expire.
/// </summary>
public sealed class ConsumableManager
{
    // Consumable candidate: itemId + ilvl for sorting
    private readonly record struct Candidate(uint ItemId, int ItemLevel, string Name);

    // Classified consumable lists, sorted by ItemLevel descending
    private readonly List<Candidate> gatheringFoods = new();
    private readonly List<Candidate> crafterFoods = new();
    private readonly List<Candidate> gatheringPots = new();
    private readonly List<Candidate> crafterPots = new();

    // (Inventory scanning now uses InventoryManager.GetInventoryItemCount)

    // Cooldown: only use one item per tick to respect animation lock
    private DateTime lastUseTime = DateTime.MinValue;
    private static readonly TimeSpan UseCooldown = TimeSpan.FromSeconds(3.5);

    // Inventory rescan interval
    private DateTime lastInventoryScan = DateTime.MinValue;
    private static readonly TimeSpan InventoryScanInterval = TimeSpan.FromSeconds(30);

    // Buff expiry threshold — re-eat/re-pot when under this many seconds remaining
    private const float ExpiryThresholdSeconds = 60f;

    // Cached inventory results
    private (uint ItemId, bool IsHq)? cachedFood;
    private (uint ItemId, bool IsHq)? cachedPot;
    private bool inventoryDirty = true;

    // Queued immediate consumption (set from UI thread, executed on framework thread)
    private bool consumeNowPending;
    private bool consumeNowIsGatherer;
    private bool consumeNowAutoFood;
    private bool consumeNowAutoPots;
    private BuffTracker? consumeNowBuffTracker;

    // Status properties for UI display
    public string? LastFoodUsed { get; private set; }
    public string? LastPotUsed { get; private set; }
    public bool HasGatheringFood => gatheringFoods.Count > 0;
    public bool HasCrafterFood => crafterFoods.Count > 0;
    public bool HasGatheringPot => gatheringPots.Count > 0;
    public bool HasCrafterPot => crafterPots.Count > 0;
    public bool DatabaseReady { get; private set; }

    /// <summary>
    /// Builds the consumable database from Lumina Item + ItemFood sheets.
    /// Call once during plugin init.
    /// </summary>
    public void BuildDatabase()
    {
        gatheringFoods.Clear();
        crafterFoods.Clear();
        gatheringPots.Clear();
        crafterPots.Clear();

        var itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>();
        var itemFoodSheet = DalamudApi.DataManager.GetExcelSheet<ItemFood>();
        if (itemSheet == null || itemFoodSheet == null)
        {
            DalamudApi.Log.Warning("[ConsumableManager] Failed to load Lumina sheets.");
            return;
        }

        foreach (var item in itemSheet)
        {
            try
            {
                var uiCategoryId = item.ItemUICategory.RowId;
                // 46 = Meal, 44 = Medicine
                if (uiCategoryId is not 46 and not 44) continue;
                if (!item.ItemAction.IsValid) continue;

                var action = item.ItemAction.Value;
                var actionParams = action.Data; // [0] = status effect id, [1] = ItemFood row, [2] = duration
                // 48 = Well Fed (food), 49 = Medicated (medicine)
                if (actionParams[0] is not 48 and not 49) continue;

                var foodRow = itemFoodSheet.GetRowOrDefault(actionParams[1]);
                if (foodRow == null) continue;

                var food = foodRow.Value;
                var isMeal = uiCategoryId == 46;
                var isMedicine = uiCategoryId == 44;
                var ilvl = (int)item.LevelItem.RowId;
                var name = item.Name.ToString();
                var candidate = new Candidate(item.RowId, ilvl, name);

                // Check which stats this consumable provides
                var hasGatherStats = false;
                var hasCraftStats = false;
                foreach (var p in food.Params)
                {
                    var paramId = p.BaseParam.RowId;
                    // GP=10, Gathering=72, Perception=73
                    if (paramId is 10 or 72 or 73) hasGatherStats = true;
                    // CP=11, Craftsmanship=70, Control=71
                    if (paramId is 11 or 70 or 71) hasCraftStats = true;
                }

                if (isMeal && hasGatherStats) gatheringFoods.Add(candidate);
                if (isMeal && hasCraftStats) crafterFoods.Add(candidate);
                if (isMedicine && hasGatherStats) gatheringPots.Add(candidate);
                if (isMedicine && hasCraftStats) crafterPots.Add(candidate);
            }
            catch
            {
                // Skip items that fail to parse
            }
        }

        // Sort all lists by item level descending (highest ilvl first)
        gatheringFoods.Sort((a, b) => b.ItemLevel.CompareTo(a.ItemLevel));
        crafterFoods.Sort((a, b) => b.ItemLevel.CompareTo(a.ItemLevel));
        gatheringPots.Sort((a, b) => b.ItemLevel.CompareTo(a.ItemLevel));
        crafterPots.Sort((a, b) => b.ItemLevel.CompareTo(a.ItemLevel));

        DatabaseReady = true;
        DalamudApi.Log.Information(
            $"[ConsumableManager] Database built: {gatheringFoods.Count} gather foods, " +
            $"{crafterFoods.Count} crafter foods, {gatheringPots.Count} gather pots, " +
            $"{crafterPots.Count} crafter pots.");
    }

    /// <summary>
    /// Main tick called from CosmicTab.PollIceState() every ~1s.
    /// Checks food and medicine buffs and auto-uses consumables as needed.
    /// </summary>
    // Throttle debug logging to avoid log spam (once per 10s)
    private DateTime lastDebugLog = DateTime.MinValue;
    private static readonly TimeSpan DebugLogInterval = TimeSpan.FromSeconds(10);

    public void Update(BuffTracker buffTracker, bool isGatherer, bool autoFood, bool autoPots)
    {
        // Process queued ConsumeNow first (from session start, needs framework thread)
        if (consumeNowPending)
        {
            ExecuteConsumeNow();
            return; // Let the item usage settle before normal processing
        }

        if (!DatabaseReady) return;

        var now = DateTime.UtcNow;
        var shouldDebugLog = now - lastDebugLog > DebugLogInterval;

        if (!CanUseConsumables())
        {
            if (shouldDebugLog)
            {
                lastDebugLog = now;
                DalamudApi.Log.Debug("[ConsumableManager] Update skipped: player occupied");
            }
            return;
        }

        // Respect animation lock cooldown — only one item per tick
        if (now - lastUseTime < UseCooldown) return;

        // Rescan inventory periodically or when marked dirty
        if (inventoryDirty || now - lastInventoryScan > InventoryScanInterval)
        {
            RescanInventory(isGatherer);
            lastInventoryScan = now;
            inventoryDirty = false;
            DalamudApi.Log.Information(
                $"[ConsumableManager] Inventory scanned: isGatherer={isGatherer}, " +
                $"food={cachedFood?.ItemId.ToString() ?? "none"} (hq={cachedFood?.IsHq}), " +
                $"pot={cachedPot?.ItemId.ToString() ?? "none"} (hq={cachedPot?.IsHq})");
        }

        // Try food first
        if (autoFood)
        {
            var foodRemaining = buffTracker.GetFoodBuffRemainingSeconds();
            if (foodRemaining < ExpiryThresholdSeconds)
            {
                if (cachedFood.HasValue)
                {
                    var (itemId, isHq) = cachedFood.Value;
                    if (TryUseItem(itemId, isHq))
                    {
                        var candidates = isGatherer ? gatheringFoods : crafterFoods;
                        var name = candidates.FirstOrDefault(c => c.ItemId == itemId).Name ?? $"Item #{itemId}";
                        LastFoodUsed = isHq ? $"{name} (HQ)" : name;
                        lastUseTime = now;
                        inventoryDirty = true;
                        DalamudApi.Log.Information($"[ConsumableManager] Used food: {LastFoodUsed}");
                        return; // One item per tick
                    }
                    else if (shouldDebugLog)
                    {
                        DalamudApi.Log.Debug($"[ConsumableManager] TryUseItem failed for food {itemId} (hq={isHq})");
                    }
                }
                else if (shouldDebugLog)
                {
                    DalamudApi.Log.Debug("[ConsumableManager] Food buff low but no food cached in inventory");
                }
            }
        }

        // Try medicine/pots
        if (autoPots)
        {
            var medRemaining = buffTracker.GetMedicineBuffRemainingSeconds();
            if (medRemaining < ExpiryThresholdSeconds)
            {
                if (cachedPot.HasValue)
                {
                    var (itemId, isHq) = cachedPot.Value;
                    if (TryUseItem(itemId, isHq))
                    {
                        var candidates = isGatherer ? gatheringPots : crafterPots;
                        var name = candidates.FirstOrDefault(c => c.ItemId == itemId).Name ?? $"Item #{itemId}";
                        LastPotUsed = isHq ? $"{name} (HQ)" : name;
                        lastUseTime = now;
                        inventoryDirty = true;
                        DalamudApi.Log.Information($"[ConsumableManager] Used medicine: {LastPotUsed}");
                    }
                    else if (shouldDebugLog)
                    {
                        DalamudApi.Log.Debug($"[ConsumableManager] TryUseItem failed for pot {itemId} (hq={isHq})");
                    }
                }
                else if (shouldDebugLog)
                {
                    lastDebugLog = now;
                    DalamudApi.Log.Debug("[ConsumableManager] Medicine buff low but no pot cached in inventory");
                }
            }
        }
    }

    /// <summary>
    /// Queues an immediate consumption attempt for the next framework tick.
    /// Call from UI thread at session start — the actual UseAction runs on the
    /// framework thread where game actions are allowed.
    /// </summary>
    public void ConsumeNow(BuffTracker buffTracker, bool isGatherer, bool autoFood, bool autoPots)
    {
        consumeNowPending = true;
        consumeNowBuffTracker = buffTracker;
        consumeNowIsGatherer = isGatherer;
        consumeNowAutoFood = autoFood;
        consumeNowAutoPots = autoPots;
        DalamudApi.Log.Information(
            $"[ConsumableManager] ConsumeNow queued: isGatherer={isGatherer}, food={autoFood}, pots={autoPots}");
    }

    /// <summary>
    /// Executes a queued ConsumeNow on the framework thread.
    /// Called from Update() before normal processing.
    /// </summary>
    private void ExecuteConsumeNow()
    {
        consumeNowPending = false;
        var buffTracker = consumeNowBuffTracker;
        if (buffTracker == null || !DatabaseReady) return;

        var isGatherer = consumeNowIsGatherer;
        RescanInventory(isGatherer);
        lastInventoryScan = DateTime.UtcNow;
        inventoryDirty = false;

        var foodBuff = buffTracker.GetFoodBuffRemainingSeconds();
        var medBuff = buffTracker.GetMedicineBuffRemainingSeconds();
        DalamudApi.Log.Information(
            $"[ConsumableManager] ConsumeNow executing: isGatherer={isGatherer}, " +
            $"food={cachedFood?.ItemId.ToString() ?? "none"} (hq={cachedFood?.IsHq}), " +
            $"pot={cachedPot?.ItemId.ToString() ?? "none"} (hq={cachedPot?.IsHq}), " +
            $"foodBuff={foodBuff:F1}s, medBuff={medBuff:F1}s, threshold={ExpiryThresholdSeconds}s");

        if (consumeNowAutoFood && foodBuff < ExpiryThresholdSeconds && cachedFood.HasValue)
        {
            var (itemId, isHq) = cachedFood.Value;
            if (TryUseItem(itemId, isHq))
            {
                var candidates = isGatherer ? gatheringFoods : crafterFoods;
                var name = candidates.FirstOrDefault(c => c.ItemId == itemId).Name ?? $"Item #{itemId}";
                LastFoodUsed = isHq ? $"{name} (HQ)" : name;
                lastUseTime = DateTime.UtcNow;
                inventoryDirty = true;
                DalamudApi.Log.Information($"[ConsumableManager] ConsumeNow used food: {LastFoodUsed}");
            }
            else
            {
                DalamudApi.Log.Warning("[ConsumableManager] ConsumeNow TryUseItem failed for food");
            }
        }

        if (consumeNowAutoPots && medBuff < ExpiryThresholdSeconds && cachedPot.HasValue)
        {
            var (itemId, isHq) = cachedPot.Value;
            if (TryUseItem(itemId, isHq))
            {
                var candidates = isGatherer ? gatheringPots : crafterPots;
                var name = candidates.FirstOrDefault(c => c.ItemId == itemId).Name ?? $"Item #{itemId}";
                LastPotUsed = isHq ? $"{name} (HQ)" : name;
                lastUseTime = DateTime.UtcNow;
                inventoryDirty = true;
                DalamudApi.Log.Information($"[ConsumableManager] ConsumeNow used pot: {LastPotUsed}");
            }
            else
            {
                DalamudApi.Log.Warning("[ConsumableManager] ConsumeNow TryUseItem failed for pot");
            }
        }

        consumeNowBuffTracker = null;
    }

    /// <summary>
    /// Resets session state. Call on session start/stop.
    /// </summary>
    public void Reset()
    {
        LastFoodUsed = null;
        LastPotUsed = null;
        cachedFood = null;
        cachedPot = null;
        inventoryDirty = true;
        lastUseTime = DateTime.MinValue;
        lastInventoryScan = DateTime.MinValue;
    }

    /// <summary>
    /// Rescans inventory to find the best available food and pot for the current job type.
    /// </summary>
    private void RescanInventory(bool isGatherer)
    {
        var foodList = isGatherer ? gatheringFoods : crafterFoods;
        var potList = isGatherer ? gatheringPots : crafterPots;

        cachedFood = FindBestInInventory(foodList);
        cachedPot = FindBestInInventory(potList);
    }

    /// <summary>
    /// Iterates candidates (already sorted by ilvl desc), checks HQ first then NQ
    /// using GetInventoryItemCount (same approach as ICE's PlayerHelper.GetItemCount).
    /// Returns first match.
    /// </summary>
    private static unsafe (uint ItemId, bool IsHq)? FindBestInInventory(List<Candidate> candidates)
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return null;

        foreach (var candidate in candidates)
        {
            // Check HQ first (preferred) — second param = isHq
            if (manager->GetInventoryItemCount(candidate.ItemId, true) > 0)
                return (candidate.ItemId, true);

            // Then NQ
            if (manager->GetInventoryItemCount(candidate.ItemId, false) > 0)
                return (candidate.ItemId, false);
        }

        return null;
    }

    /// <summary>
    /// Uses an item via ActionManager. HQ items use itemId + 1_000_000.
    /// Returns true if the action was successfully queued.
    /// </summary>
    private static unsafe bool TryUseItem(uint itemId, bool isHq)
    {
        var am = ActionManager.Instance();
        if (am == null)
        {
            DalamudApi.Log.Warning("[ConsumableManager] TryUseItem: ActionManager is null");
            return false;
        }

        // HQ items use itemId + 1_000_000 for UseAction
        var useId = isHq ? itemId + 1_000_000 : itemId;

        DalamudApi.Log.Information(
            $"[ConsumableManager] TryUseItem: itemId={itemId}, isHq={isHq}, useId={useId}");

        // Follow ICE's pattern: just call UseAction directly.
        // ICE never gates on GetActionStatus for food — just fires and throttles.
        return am->UseAction(ActionType.Item, useId, extraParam: 65535);
    }

    /// <summary>
    /// Checks if the player is in a state where consumable usage is safe.
    /// Blocks during crafting/gathering/fishing and all occupied states.
    /// Consumables will be used during transition windows (walking between tasks,
    /// waiting for missions, etc.) — not mid-craft or mid-gather.
    /// </summary>
    private static bool CanUseConsumables()
    {
        var player = DalamudApi.ObjectTable.LocalPlayer;
        if (player == null) return false;

        var cond = DalamudApi.Condition;
        if (cond[ConditionFlag.Crafting]) return false;
        if (cond[ConditionFlag.PreparingToCraft]) return false;
        if (cond[ConditionFlag.ExecutingCraftingAction]) return false;
        if (cond[ConditionFlag.Gathering]) return false;
        if (cond[ConditionFlag.Fishing]) return false;
        if (cond[ConditionFlag.Occupied]) return false;
        if (cond[ConditionFlag.Occupied30]) return false;
        if (cond[ConditionFlag.Occupied33]) return false;
        if (cond[ConditionFlag.Occupied38]) return false;
        if (cond[ConditionFlag.Occupied39]) return false;
        if (cond[ConditionFlag.OccupiedInCutSceneEvent]) return false;
        if (cond[ConditionFlag.OccupiedInQuestEvent]) return false;
        if (cond[ConditionFlag.BetweenAreas]) return false;
        if (cond[ConditionFlag.BetweenAreas51]) return false;
        if (cond[ConditionFlag.InCombat]) return false;
        if (cond[ConditionFlag.Mounted]) return false;
        if (cond[ConditionFlag.Casting]) return false;

        return true;
    }
}
