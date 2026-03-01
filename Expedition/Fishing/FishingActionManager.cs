using FFXIVClientStructs.FFXIV.Client.Game;

namespace Expedition.Fishing;

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
}
