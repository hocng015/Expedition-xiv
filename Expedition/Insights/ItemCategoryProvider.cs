using Lumina.Excel.Sheets;

namespace Expedition.Insights;

/// <summary>
/// Provides curated sets of item IDs for market analysis.
/// Uses Lumina sheets to identify items by category rather than hard-coding IDs,
/// so the plugin stays current across game patches.
///
/// Categories are defined by ItemSearchCategory RowIds from the game's
/// market board category system. These are stable across patches (the
/// categories themselves don't change, only the items within them).
/// </summary>
public sealed class ItemCategoryProvider
{
    /// <summary>Max items per category to query from Universalis.</summary>
    private const int MaxItemsPerCategory = 80;

    /// <summary>
    /// Minimum item level for consumable/gear-adjacent categories.
    /// Items below this threshold are filtered out to focus on current endgame tier.
    /// Crystals and Dyes are exempt since they don't have meaningful item levels.
    /// Set to ~580+ to capture Dawntrail 7.x tier items.
    /// </summary>
    private const int MinItemLevelForEndgame = 580;

    /// <summary>
    /// Categories that should NOT be filtered by item level.
    /// These contain items with meaningless item levels (e.g., crystals are always iLvl 1).
    /// </summary>
    private static readonly HashSet<string> ItemLevelExemptCategories = new()
    {
        "Crystals", "Dyes", "Furnishings", "Gardening",
        "Minions", "Orchestrion Rolls", "Mounts & Bardings",
        "Seasonal", "Miscellany", "Ingredients",
    };

    /// <summary>
    /// Category definitions using ItemSearchCategory RowIds.
    /// Reference: https://v2.xivapi.com/api/sheet/ItemSearchCategory
    /// </summary>
    private static readonly (string Name, int[] SearchCategoryIds)[] CategoryDefinitions =
    {
        ("Meals",               new[] { 45, 46 }),                                           // Meals, Seafood
        ("Medicine",            new[] { 43 }),                                                // Tinctures, potions
        ("Ingredients",         new[] { 44 }),                                                // Cooking/alchemy ingredients
        ("Materia",             new[] { 57 }),                                                // All materia
        ("Crafting Mats",       new[] { 47, 48, 49, 50, 51, 52, 53, 55, 56 }),              // Stone thru Furnishing mats
        ("Gear",                new[] { 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42 }), // Head thru Rings
        ("Dyes",                new[] { 54 }),                                                // Dyes
        ("Furnishings",         new[] { 65, 66, 67, 68, 69, 70, 71, 72 }),                  // Housing furnishings
        ("Crystals",            new[] { 58, 59 }),                                            // Crystals, Catalysts
        ("Minions",             new[] { 75 }),                                                // Minions
        ("Mounts & Bardings",   new[] { 90 }),                                                // Registrable miscellany
    };

    private readonly Dictionary<string, List<uint>> categoryItemIds = new();

    public ItemCategoryProvider()
    {
        BuildCategories();
    }

    /// <summary>
    /// Returns the curated item IDs for each category.
    /// </summary>
    public IReadOnlyDictionary<string, List<uint>> GetCategories() => categoryItemIds;

    /// <summary>
    /// Returns all category names in definition order.
    /// </summary>
    public string[] GetCategoryNames()
    {
        var names = new string[CategoryDefinitions.Length];
        for (var i = 0; i < CategoryDefinitions.Length; i++)
            names[i] = CategoryDefinitions[i].Name;
        return names;
    }

    private void BuildCategories()
    {
        try
        {
            var itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>();
            if (itemSheet == null) return;

            // Build a fast lookup: SearchCategoryId -> CategoryName
            var catLookup = new Dictionary<int, string>();
            foreach (var (catName, catIds) in CategoryDefinitions)
            {
                for (var i = 0; i < catIds.Length; i++)
                    catLookup[catIds[i]] = catName;
            }

            // Diagnostic: check if specific current-tier items exist in the Lumina sheet
            uint[] diagnosticItemIds = { 49240, 49239, 46007, 44175 }; // Caramel Popcorn, Nachos, Mollete, Roast Chicken
            foreach (var diagId in diagnosticItemIds)
            {
                var diagItem = itemSheet.GetRowOrDefault(diagId);
                if (diagItem != null)
                {
                    var diagName = diagItem.Value.Name.ExtractText();
                    var diagCat = (int)diagItem.Value.ItemSearchCategory.RowId;
                    var diagIlvl = (int)diagItem.Value.LevelItem.RowId;
                    DalamudApi.Log.Information(
                        $"[Insights] Diagnostic: Item {diagId} = '{diagName}' SearchCat={diagCat} iLvl={diagIlvl}");
                }
                else
                {
                    DalamudApi.Log.Warning(
                        $"[Insights] Diagnostic: Item {diagId} NOT FOUND in Lumina Item sheet!");
                }
            }

            // Single pass through the Item sheet, storing (RowId, ItemLevel) pairs per category
            var rawItems = new Dictionary<string, List<(uint RowId, int ItemLevel)>>();
            var totalSheetRows = 0;

            foreach (var item in itemSheet)
            {
                totalSheetRows++;
                var searchCatId = (int)item.ItemSearchCategory.RowId;
                if (searchCatId == 0) continue; // Not marketable

                if (!catLookup.TryGetValue(searchCatId, out var catName))
                    continue;

                var name = item.Name.ExtractText();
                if (string.IsNullOrEmpty(name)) continue;

                if (!rawItems.TryGetValue(catName, out var list))
                {
                    list = new List<(uint, int)>();
                    rawItems[catName] = list;
                }

                var itemLevel = (int)item.LevelItem.RowId;
                list.Add((item.RowId, itemLevel));
            }

            // Filter and trim each category
            foreach (var (catName, items) in rawItems)
            {
                List<uint> finalIds;

                if (ItemLevelExemptCategories.Contains(catName))
                {
                    // No item-level filter — just trim by RowId descending (newest first)
                    items.Sort((a, b) => b.RowId.CompareTo(a.RowId));
                    var count = Math.Min(MaxItemsPerCategory, items.Count);
                    finalIds = new List<uint>(count);
                    for (var i = 0; i < count; i++)
                        finalIds.Add(items[i].RowId);
                }
                else
                {
                    // Filter to endgame items by item level, then trim by RowId descending
                    var endgameItems = new List<(uint RowId, int ItemLevel)>();
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (items[i].ItemLevel >= MinItemLevelForEndgame)
                            endgameItems.Add(items[i]);
                    }

                    // Fallback: if iLvl filtering is too aggressive, take top N by RowId instead
                    if (endgameItems.Count < 10)
                    {
                        DalamudApi.Log.Information(
                            $"[Insights] Category '{catName}' has only {endgameItems.Count} endgame items (iLvl >= {MinItemLevelForEndgame}), falling back to newest items by RowId");
                        items.Sort((a, b) => b.RowId.CompareTo(a.RowId));
                        var count = Math.Min(MaxItemsPerCategory, items.Count);
                        finalIds = new List<uint>(count);
                        for (var i = 0; i < count; i++)
                            finalIds.Add(items[i].RowId);
                    }
                    else
                    {
                        // Sort endgame items by item level descending, then RowId descending
                        endgameItems.Sort((a, b) =>
                        {
                            var cmp = b.ItemLevel.CompareTo(a.ItemLevel);
                            return cmp != 0 ? cmp : b.RowId.CompareTo(a.RowId);
                        });

                        var count = Math.Min(MaxItemsPerCategory, endgameItems.Count);
                        finalIds = new List<uint>(count);
                        for (var i = 0; i < count; i++)
                            finalIds.Add(endgameItems[i].RowId);
                    }
                }

                categoryItemIds[catName] = finalIds;

                DalamudApi.Log.Information(
                    $"[Insights] Category '{catName}': {items.Count} total -> {finalIds.Count} selected");

                // Log the top 5 items by RowId for diagnostic verification
                if (finalIds.Count > 0)
                {
                    var topCount = Math.Min(5, finalIds.Count);
                    var topIds = new System.Text.StringBuilder("[Insights]   Top IDs: ");
                    for (var k = 0; k < topCount; k++)
                    {
                        if (k > 0) topIds.Append(", ");
                        topIds.Append(finalIds[k]);
                    }
                    DalamudApi.Log.Information(topIds.ToString());
                }
            }

            var totalItems = 0;
            foreach (var kvp in categoryItemIds)
                totalItems += kvp.Value.Count;

            DalamudApi.Log.Information(
                $"[Insights] Lumina Item sheet has {totalSheetRows} total rows");
            DalamudApi.Log.Information(
                $"[Insights] Built {categoryItemIds.Count} categories with {totalItems} total items");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "[Insights] Failed to build item categories from Lumina");
        }
    }
}
