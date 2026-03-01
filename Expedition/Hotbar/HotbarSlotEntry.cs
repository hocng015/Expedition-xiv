using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace Expedition.Hotbar;

/// <summary>
/// A single hotbar slot assignment used by all job hotbar configs.
/// </summary>
public readonly record struct HotbarSlotEntry(
    int SlotIndex,
    uint ActionId,
    string ActionName,
    int RequiredLevel,
    RaptureHotbarModule.HotbarSlotType SlotType = RaptureHotbarModule.HotbarSlotType.Action
);
