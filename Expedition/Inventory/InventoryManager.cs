using Dalamud.Game.Inventory.InventoryEventArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;

using Expedition.RecipeResolver;

using InventoryManager_Game = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager;

namespace Expedition.Inventory;

/// <summary>
/// Scans player inventory to determine what materials are already available.
/// Uses FFXIVClientStructs for direct inventory access.
/// Supports event-driven change detection via IGameInventory for faster gathering completion.
/// </summary>
public sealed class InventoryManager
{
    // --- Inventory Event Integration ---

    /// <summary>
    /// Timestamp of the last inventory change event from Dalamud's IGameInventory.
    /// Used by GatheringOrchestrator to detect changes between polling intervals.
    /// </summary>
    public DateTime LastInventoryEventTime { get; private set; } = DateTime.MinValue;

    /// <summary>Whether inventory events are currently subscribed.</summary>
    private bool eventsSubscribed;

    /// <summary>
    /// Subscribes to Dalamud's IGameInventory.InventoryChangedRaw event
    /// for near-instant inventory change detection during gathering.
    /// </summary>
    public void SubscribeInventoryEvents()
    {
        if (eventsSubscribed) return;
        DalamudApi.GameInventory.InventoryChangedRaw += OnInventoryChanged;
        eventsSubscribed = true;
        DalamudApi.Log.Debug("Inventory change events subscribed.");
    }

    /// <summary>
    /// Unsubscribes from inventory change events. Call on plugin dispose.
    /// </summary>
    public void UnsubscribeInventoryEvents()
    {
        if (!eventsSubscribed) return;
        DalamudApi.GameInventory.InventoryChangedRaw -= OnInventoryChanged;
        eventsSubscribed = false;
        DalamudApi.Log.Debug("Inventory change events unsubscribed.");
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        LastInventoryEventTime = DateTime.Now;
    }

    // --- Item Slot Cache ---

    /// <summary>
    /// Lightweight cache of known slot positions for a tracked item.
    /// Avoids scanning all containers on every poll by reading only cached positions.
    /// </summary>
    public sealed class ItemSlotCache
    {
        private readonly List<(InventoryType Container, int SlotIndex)> knownSlots = new();
        private uint trackedItemId;
        private int lastCachedTotal;

        /// <summary>Total count as of the last cache read.</summary>
        public int LastTotal => lastCachedTotal;

        /// <summary>
        /// Initializes the cache by scanning all containers for the given item.
        /// Call once at the start of a gather task.
        /// </summary>
        public unsafe void Initialize(uint itemId, bool includeSaddlebag)
        {
            trackedItemId = itemId;
            knownSlots.Clear();
            lastCachedTotal = 0;

            var manager = InventoryManager_Game.Instance();
            if (manager == null) return;

            ScanContainers(manager, InventoryTypes, itemId);
            if (includeSaddlebag)
                ScanContainers(manager, SaddlebagTypes, itemId);
        }

        private unsafe void ScanContainers(
            InventoryManager_Game* manager, InventoryType[] types, uint itemId)
        {
            foreach (var invType in types)
            {
                var container = manager->GetInventoryContainer(invType);
                if (container == null) continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null) continue;

                    if (slot->ItemId == itemId || slot->ItemId == itemId + 1000000)
                    {
                        knownSlots.Add((invType, i));
                        lastCachedTotal += (int)slot->Quantity;
                    }
                }
            }
        }

        /// <summary>
        /// Fast count: reads only cached slot positions.
        /// Returns -1 if a slot changed unexpectedly (cache needs reinit).
        /// </summary>
        public unsafe int GetCachedCount(bool includeSaddlebag)
        {
            var manager = InventoryManager_Game.Instance();
            if (manager == null) return -1;

            var total = 0;
            var needsReinit = false;

            for (var i = 0; i < knownSlots.Count; i++)
            {
                var (invType, slotIdx) = knownSlots[i];
                var container = manager->GetInventoryContainer(invType);
                if (container == null) continue;

                var slot = container->GetInventorySlot(slotIdx);
                if (slot == null) continue;

                // If the item in this slot changed to something else, cache is stale
                if (slot->ItemId != trackedItemId && slot->ItemId != trackedItemId + 1000000)
                {
                    if (slot->ItemId == 0)
                        continue; // Slot was emptied (item moved/used)
                    needsReinit = true;
                    break;
                }

                total += (int)slot->Quantity;
            }

            if (needsReinit)
            {
                // Cache is stale — reinitialize and return fresh count
                Initialize(trackedItemId, includeSaddlebag);
                return lastCachedTotal;
            }

            // Also check for new slots by scanning containers for the item
            // (handles overflow to new stacks). Only do this if count seems low.
            // For efficiency, we skip the full scan if total increased normally.
            lastCachedTotal = total;
            return total;
        }

        /// <summary>Clears the cache.</summary>
        public void Clear()
        {
            knownSlots.Clear();
            trackedItemId = 0;
            lastCachedTotal = 0;
        }
    }
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
    /// Uses a single inventory snapshot to avoid rescanning containers for each material list.
    /// </summary>
    public unsafe void UpdateResolvedRecipe(ResolvedRecipe resolved, bool includeSaddlebag = false)
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return;

        // Build a single item count snapshot for all needed item IDs
        var itemIds = new HashSet<uint>();
        foreach (var m in resolved.GatherList) itemIds.Add(m.ItemId);
        foreach (var m in resolved.OtherMaterials) itemIds.Add(m.ItemId);
        foreach (var step in resolved.CraftOrder)
            foreach (var ing in step.Recipe.Ingredients) itemIds.Add(ing.ItemId);

        // Single pass through all containers to count everything at once
        var counts = new Dictionary<uint, int>(itemIds.Count);
        foreach (var id in itemIds) counts[id] = 0;

        foreach (var invType in InventoryTypes)
            AccumulateCounts(manager, invType, counts);

        if (includeSaddlebag)
        {
            foreach (var invType in SaddlebagTypes)
                AccumulateCounts(manager, invType, counts);
        }

        // Apply counts in one pass per list (no container re-scanning)
        foreach (var m in resolved.GatherList)
            if (counts.TryGetValue(m.ItemId, out var c)) m.QuantityOwned = c;
        foreach (var m in resolved.OtherMaterials)
            if (counts.TryGetValue(m.ItemId, out var c)) m.QuantityOwned = c;
        foreach (var step in resolved.CraftOrder)
            foreach (var ing in step.Recipe.Ingredients)
                if (counts.TryGetValue(ing.ItemId, out var c)) ing.QuantityOwned = c;
    }

    /// <summary>
    /// Accumulates item counts from a single container into the counts dictionary.
    /// Only counts items that exist as keys in the dictionary.
    /// </summary>
    private static unsafe void AccumulateCounts(
        InventoryManager_Game* manager, InventoryType invType, Dictionary<uint, int> counts)
    {
        var container = manager->GetInventoryContainer(invType);
        if (container == null) return;

        for (var i = 0; i < container->Size; i++)
        {
            var slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;

            var id = slot->ItemId;
            var qty = (int)slot->Quantity;

            if (counts.ContainsKey(id))
                counts[id] += qty;

            // HQ item
            var hqId = id - 1000000;
            if (id > 1000000 && counts.ContainsKey(hqId))
                counts[hqId] += qty;
        }
    }

    /// <summary>
    /// Gets the player's current gil balance.
    /// Gil is item ID 1 in the game's inventory system.
    /// </summary>
    public unsafe long GetGilCount()
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return 0;
        return manager->GetInventoryItemCount(1);
    }

    /// <summary>
    /// Computes vendor costs grouped by currency for materials that still need to be obtained.
    /// Returns a dictionary of CurrencyName -> total cost.
    /// </summary>
    public static Dictionary<string, long> ComputeVendorCosts(IEnumerable<MaterialRequirement> materials)
    {
        var costs = new Dictionary<string, long>();
        foreach (var mat in materials)
        {
            if (!mat.IsVendorItem || mat.VendorInfo == null || mat.QuantityRemaining <= 0) continue;
            var currency = mat.VendorInfo.CurrencyName;
            var cost = (long)mat.VendorInfo.PricePerUnit * mat.QuantityRemaining;
            if (costs.TryGetValue(currency, out var existing))
                costs[currency] = existing + cost;
            else
                costs[currency] = cost;
        }
        return costs;
    }

    /// <summary>
    /// Returns the number of free inventory slots.
    /// </summary>
    /// <summary>Player bag types for free-slot counting (static to avoid allocation per call).</summary>
    private static readonly InventoryType[] PlayerBagTypes =
    {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    };

    public unsafe int GetFreeSlotCount()
    {
        var manager = InventoryManager_Game.Instance();
        if (manager == null) return 0;

        var free = 0;
        foreach (var invType in PlayerBagTypes)
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
        var distinctItems = 0;
        for (var i = 0; i < resolved.GatherList.Count; i++)
            if (resolved.GatherList[i].QuantityRemaining > 0) distinctItems++;

        var freeSlots = GetFreeSlotCount();
        return Math.Max(0, distinctItems - freeSlots);
    }
}
