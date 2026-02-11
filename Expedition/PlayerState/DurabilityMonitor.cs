using FFXIVClientStructs.FFXIV.Client.Game;

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
    /// <summary>
    /// Scans all equipped gear and returns the lowest durability percentage.
    /// </summary>
    public unsafe DurabilityReport GetReport()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return new DurabilityReport { LowestPercent = 100, LowestItemName = "Unknown" };

        var equippedContainer = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (equippedContainer == null || equippedContainer->Loaded == 0)
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
            if (conditionPercent < lowestPercent)
            {
                lowestPercent = conditionPercent;
                // Would need Lumina lookup for name; use slot index for now
                lowestName = $"Equipment slot {i}";
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
        var report = GetReport();
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

        // Grade 8 Dark Matter
        const uint darkMatter8 = 33916;
        // Also check older grades as fallback
        uint[] darkMatterIds = { 33916, 21800, 12884, 10386, 7968, 5595, 5594, 5593 };

        var types = new[] {
            InventoryType.Inventory1, InventoryType.Inventory2,
            InventoryType.Inventory3, InventoryType.Inventory4,
        };

        foreach (var invType in types)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null || container->Loaded == 0) continue;

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
            if (LowestPercent > 50) return $"Durability: {LowestPercent}% (OK)";
            if (LowestPercent > 20) return $"Durability: {LowestPercent}% (Low - consider repair)";
            if (LowestPercent > 0) return $"Durability: {LowestPercent}% (CRITICAL - repair needed!)";
            return "Durability: 0% (BROKEN - repair required!)";
        }
    }
}
