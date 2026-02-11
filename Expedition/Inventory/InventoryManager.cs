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
    /// Inventory types to scan for materials.
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
    /// Gets the total count of an item across all inventory bags.
    /// </summary>
    public unsafe int GetItemCount(uint itemId, bool includeHq = true)
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return 0;

        var count = 0;
        foreach (var invType in InventoryTypes)
        {
            var container = manager->GetInventoryContainer(invType);
            if (container == null || container->Loaded == 0) continue;

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
        }

        return count;
    }

    /// <summary>
    /// Updates the QuantityOwned field on each material requirement
    /// based on current inventory state.
    /// </summary>
    public void UpdateOwnedQuantities(IEnumerable<MaterialRequirement> materials)
    {
        foreach (var mat in materials)
        {
            mat.QuantityOwned = GetItemCount(mat.ItemId);
        }
    }

    /// <summary>
    /// Updates owned quantities for the entire resolved recipe.
    /// </summary>
    public void UpdateResolvedRecipe(ResolvedRecipe resolved)
    {
        UpdateOwnedQuantities(resolved.GatherList);
        UpdateOwnedQuantities(resolved.OtherMaterials);

        // Also update owned for intermediate craft ingredients
        foreach (var step in resolved.CraftOrder)
        {
            UpdateOwnedQuantities(step.Recipe.Ingredients);
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
            if (container == null || container->Loaded == 0) continue;

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
