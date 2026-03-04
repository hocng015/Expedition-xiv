using FFXIVClientStructs.FFXIV.Client.Game;

using InventoryManager_Game = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;

namespace Expedition.Fishing;

public enum CordialResult
{
    None,
    Cordial,
    HiCordial,
}

/// <summary>
/// Thin unsafe wrapper around ActionManager for fishing-specific actions.
/// </summary>
public static unsafe class FishingActionManager
{
    // Fishing action IDs
    public const uint Cast = 289;
    public const uint Hook = 296;
    public const uint PatienceII = 4106;
    public const uint Chum = 4104;
    public const uint ThaliaksFavor = 26804;
    public const uint MakeshiftBait = 299;
    public const uint FishEyes = 4105;
    public const uint DoubleHook = 269;
    public const uint TripleHook = 27523;

    // Cordial item IDs
    public const uint Cordial = 6141;
    public const uint HiCordial = 12669;

    public static bool UseAction(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->UseAction(ActionType.Action, actionId);
    }

    public static bool CanUseAction(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null) return false;
        return am->GetActionStatus(ActionType.Action, actionId) == 0;
    }

    public static float GetRecastTime(uint actionId)
    {
        var am = ActionManager.Instance();
        if (am == null) return 999f;

        var group = am->GetRecastGroup((int)ActionType.Action, actionId);
        var timer = am->GetRecastGroupDetail(group);
        if (timer == null) return 0f;

        var remaining = timer->Total - timer->Elapsed;
        return remaining > 0 ? remaining : 0f;
    }

    /// <summary>
    /// Attempts to use a cordial from inventory. Prefers Hi-Cordial or Cordial based on config.
    /// Checks HQ first, then NQ. Returns which cordial was used (or None).
    /// Follows ConsumableManager.TryUseItem pattern (ActionType.Item, extraParam: 65535).
    /// </summary>
    public static CordialResult TryUseCordial(bool preferHi)
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return CordialResult.None;

        var am = ActionManager.Instance();
        if (am == null) return CordialResult.None;

        // Order based on preference
        uint first = preferHi ? HiCordial : Cordial;
        uint second = preferHi ? Cordial : HiCordial;

        foreach (var itemId in new[] { first, second })
        {
            var hasHq = manager->GetInventoryItemCount(itemId, true) > 0;
            var hasNq = manager->GetInventoryItemCount(itemId, false) > 0;

            if (!hasHq && !hasNq) continue;

            // HQ items use itemId + 1_000_000
            var useId = hasHq ? itemId + 1_000_000 : itemId;

            if (am->UseAction(ActionType.Item, useId, extraParam: 65535))
            {
                var result = itemId == HiCordial ? CordialResult.HiCordial : CordialResult.Cordial;
                DalamudApi.Log.Information($"[Fishing] Used {result} (id={itemId}, hq={hasHq})");
                return result;
            }
        }

        return CordialResult.None;
    }

    /// <summary>
    /// Gets the total inventory count (NQ + HQ) for a cordial item.
    /// </summary>
    public static int GetCordialCount(uint itemId)
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return 0;
        return manager->GetInventoryItemCount(itemId, false) + manager->GetInventoryItemCount(itemId, true);
    }
}
