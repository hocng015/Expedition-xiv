using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Expedition.PlayerState;

/// <summary>
/// Monitors gear durability to prevent crafting/gathering failure from broken equipment.
///
/// Pain points addressed:
/// - At 0% durability, gear provides zero stats → gathering/crafting fails completely
/// - Extended crafting sessions (hundreds of crafts) degrade all equipped gear
/// - Extended gathering degrades gathering gear
/// - Self-repair requires Dark Matter in inventory
/// - Repair can push above 100% up to 199% (extends sessions)
/// - Need to detect low durability BEFORE starting a long operation
/// </summary>
public sealed class DurabilityMonitor
{
    private readonly ExcelSheet<Item>? itemSheet;

    public DurabilityMonitor()
    {
        itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>();
    }

    /// <summary>
    /// Scans all equipped gear and returns a full durability report.
    /// </summary>
    public unsafe DurabilityReport GetReport(int warningThreshold = 50)
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return new DurabilityReport { LowestPercent = 100, LowestItemName = "Unknown" };

        var equippedContainer = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (equippedContainer == null)
            return new DurabilityReport { LowestPercent = 100, LowestItemName = "Unknown" };

        var lowestPercent = 100;
        var lowestName = "Unknown";
        var totalSlots = 0;
        var belowThreshold = 0;

        for (var i = 0; i < equippedContainer->Size; i++)
        {
            var slot = equippedContainer->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;

            totalSlots++;

            // Condition is stored as a value where 30000 = 100%
            var conditionPercent = (int)(slot->Condition / 300.0);

            if (conditionPercent < warningThreshold)
                belowThreshold++;

            if (conditionPercent < lowestPercent)
            {
                lowestPercent = conditionPercent;
                lowestName = LookupItemName(slot->ItemId) ?? GetSlotName(i);
            }
        }

        return new DurabilityReport
        {
            LowestPercent = lowestPercent,
            LowestItemName = lowestName,
            TotalEquippedSlots = totalSlots,
            SlotsBelowThreshold = belowThreshold,
        };
    }

    /// <summary>
    /// Returns true if any equipped gear is below the given durability threshold.
    /// </summary>
    public bool IsRepairNeeded(int thresholdPercent)
    {
        var report = GetReport(thresholdPercent);
        return report.LowestPercent < thresholdPercent;
    }

    /// <summary>
    /// Checks if the player has Dark Matter for self-repair.
    /// Grade 8 Dark Matter (ItemId 33916) is used for all current gear.
    /// </summary>
    public unsafe bool HasDarkMatter()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return false;

        // Dark Matter grades (8 is current, older grades for fallback)
        uint[] darkMatterIds = { 33916, 21800, 12884, 10386, 7968, 5595, 5594, 5593 };

        var types = new[] {
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
        };

        foreach (var invType in types)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;

                foreach (var dmId in darkMatterIds)
                {
                    if (slot->ItemId == dmId && slot->Quantity > 0)
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves an item name from Lumina data.
    /// </summary>
    private string? LookupItemName(uint itemId)
    {
        if (itemSheet == null) return null;
        var row = itemSheet.GetRowOrDefault(itemId);
        if (row == null) return null;
        var name = row.Value.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// Human-readable name for an equipment slot index.
    /// </summary>
    private static string GetSlotName(int slotIndex) => slotIndex switch
    {
        0 => "Main Hand",
        1 => "Off Hand",
        2 => "Head",
        3 => "Body",
        4 => "Hands",
        5 => "Waist",
        6 => "Legs",
        7 => "Feet",
        8 => "Ears",
        9 => "Neck",
        10 => "Wrists",
        11 => "Ring (Right)",
        12 => "Ring (Left)",
        13 => "Soul Crystal",
        _ => $"Slot {slotIndex}",
    };
}

public sealed class DurabilityReport
{
    public int LowestPercent { get; init; }
    public string LowestItemName { get; init; } = string.Empty;
    public int TotalEquippedSlots { get; init; }
    public int SlotsBelowThreshold { get; init; }

    public string StatusText
    {
        get
        {
            if (LowestPercent > 50) return $"Durability: {LowestPercent}% — {LowestItemName} (OK)";
            if (LowestPercent > 20) return $"Durability: {LowestPercent}% — {LowestItemName} (Low)";
            if (LowestPercent > 0) return $"Durability: {LowestPercent}% — {LowestItemName} (CRITICAL!)";
            return $"Durability: 0% — {LowestItemName} (BROKEN!)";
        }
    }
}
