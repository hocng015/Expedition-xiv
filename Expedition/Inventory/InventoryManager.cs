using FFXIVClientStructs.FFXIV.Client.Game;

using Expedition.RecipeResolver;

using InventoryManager_Game = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;

namespace Expedition.Inventory;

/// <summary>
/// Scans player inventory to determine what materials are already available.
/// Uses FFXIVClientStructs for direct inventory access.
/// </summary>
public sealed class InventoryManager
{
    /// <summary>
    /// Inventory types to scan for materials (player bags + crystals).
    /// </summary>
    private static readonly InventoryType[] InventoryTypes =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.Crystals,
    };

    /// <summary>
    /// Saddlebag inventory types (standard + premium).
    /// Data may be stale if the player hasn't opened the saddlebag since login.
    /// </summary>
    private static readonly InventoryType[] SaddlebagTypes =
    {
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    };

    /// <summary>
    /// Gets the total count of an item across all inventory bags.
    /// Optionally includes saddlebag (chocobo + premium) containers.
    /// </summary>
    public unsafe int GetItemCount(uint itemId, bool includeHq = true, bool includeSaddlebag = false)
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return 0;

        var count = 0;
        foreach (var invType in InventoryTypes)
        {
            count += CountInContainer(manager, invType, itemId, includeHq);
        }

        if (includeSaddlebag)
        {
            foreach (var invType in SaddlebagTypes)
            {
                count += CountInContainer(manager, invType, itemId, includeHq);
            }
        }

        return count;
    }

    /// <summary>
    /// Counts an item's quantity within a single inventory container.
    /// Returns 0 if the container is null (not loaded).
    /// </summary>
    private static unsafe int CountInContainer(
        InventoryManager_Game* manager, InventoryType invType, uint itemId, bool includeHq)
    {
        var container = manager->GetInventoryContainer(invType);
        if (container == null) return 0;

        var count = 0;
        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null) continue;

            if (slot->ItemId == itemId)
                count += (int)slot->Quantity;

            // HQ items have the same base itemId but a flag
            if (includeHq && slot->ItemId == itemId + 1000000)
                count += (int)slot->Quantity;
        }

        return count;
    }

    /// <summary>
    /// Updates the QuantityOwned field on each material requirement
    /// based on current inventory state.
    /// </summary>
    public void UpdateOwnedQuantities(IEnumerable<MaterialRequirement> materials, bool includeSaddlebag = false)
    {
        foreach (var mat in materials)
        {
            mat.QuantityOwned = GetItemCount(mat.ItemId, includeSaddlebag: includeSaddlebag);
        }
    }

    /// <summary>
    /// Updates owned quantities for the entire resolved recipe.
    /// </summary>
    public void UpdateResolvedRecipe(ResolvedRecipe resolved, bool includeSaddlebag = false)
    {
        UpdateOwnedQuantities(resolved.GatherList, includeSaddlebag);
        UpdateOwnedQuantities(resolved.OtherMaterials, includeSaddlebag);

        // Also update owned for intermediate craft ingredients
        foreach (var step in resolved.CraftOrder)
        {
            UpdateOwnedQuantities(step.Recipe.Ingredients, includeSaddlebag);
        }
    }

    /// <summary>
    /// Returns the number of free inventory slots.
    /// </summary>
    public unsafe int GetFreeSlotCount()
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return 0;

        var free = 0;
        var bagTypes = new[] {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        };

        foreach (var invType in bagTypes)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0)
                    free++;
            }
        }

        return free;
    }

    /// <summary>
    /// Checks whether the player has enough inventory space for the
    /// expected materials. Returns the estimated shortfall.
    /// </summary>
    public int EstimateInventoryShortfall(ResolvedRecipe resolved)
    {
        var distinctItems = resolved.GatherList
            .Where(g => g.QuantityRemaining > 0)
            .Count();

        var freeSlots = GetFreeSlotCount();
        return Math.Max(0, distinctItems - freeSlots);
    }
}
