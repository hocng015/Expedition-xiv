using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Expedition.RecipeResolver;

/// <summary>
/// Resolves recipe dependency trees using Lumina game data sheets.
/// Given a target recipe, it computes the full tree of sub-recipes and
/// raw materials, then flattens it into ordered gather and craft lists.
/// </summary>
public sealed class RecipeResolverService
{
    private readonly ExcelSheet<Recipe> recipeSheet;
    private readonly ExcelSheet<Item> itemSheet;
    private readonly ExcelSheet<GatheringItem> gatheringItemSheet;

    // Cache: ItemId -> list of recipes that produce it
    private readonly Dictionary<uint, List<Recipe>> recipesByItemId = new();

    public RecipeResolverService()
    {
        recipeSheet = DalamudApi.DataManager.GetExcelSheet<Recipe>()!;
        itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>()!;
        gatheringItemSheet = DalamudApi.DataManager.GetExcelSheet<GatheringItem>()!;

        BuildRecipeCache();
    }

    private void BuildRecipeCache()
    {
        foreach (var recipe in recipeSheet)
        {
            var itemId = recipe.ItemResult.RowId;
            if (itemId == 0) continue;

            if (!recipesByItemId.TryGetValue(itemId, out var list))
            {
                list = new List<Recipe>();
                recipesByItemId[itemId] = list;
            }
            list.Add(recipe);
        }

        DalamudApi.Log.Information($"Recipe cache built: {recipesByItemId.Count} craftable items.");
    }

    /// <summary>
    /// Searches for a recipe by item name (case-insensitive partial match).
    /// Returns the first matching RecipeNode or null.
    /// </summary>
    public RecipeNode? FindRecipeByName(string itemName)
    {
        var normalizedSearch = itemName.Trim().ToLowerInvariant();
        Recipe? bestMatch = null;

        foreach (var recipe in recipeSheet)
        {
            var resultItem = recipe.ItemResult.Value;
            if (resultItem.RowId == 0) continue;

            var name = resultItem.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            var normalizedName = name.ToLowerInvariant();

            // Exact match
            if (normalizedName == normalizedSearch)
            {
                bestMatch = recipe;
                break;
            }

            // Partial match (prefer first found)
            if (bestMatch == null && normalizedName.Contains(normalizedSearch))
            {
                bestMatch = recipe;
            }
        }

        if (bestMatch == null) return null;

        return BuildRecipeNode(bestMatch.Value);
    }

    /// <summary>
    /// Finds a recipe by its Lumina row ID.
    /// </summary>
    public RecipeNode? FindRecipeById(uint recipeId)
    {
        var recipe = recipeSheet.GetRowOrDefault(recipeId);
        if (recipe == null || recipe.Value.ItemResult.RowId == 0) return null;

        return BuildRecipeNode(recipe.Value);
    }

    /// <summary>
    /// Searches for recipes matching a query. Returns up to maxResults matches.
    /// </summary>
    public List<RecipeNode> SearchRecipes(string query, int maxResults = 50)
    {
        var results = new List<RecipeNode>();
        var normalizedQuery = query.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalizedQuery)) return results;

        foreach (var recipe in recipeSheet)
        {
            if (results.Count >= maxResults) break;

            var resultItem = recipe.ItemResult.Value;
            if (resultItem.RowId == 0) continue;

            var name = resultItem.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            if (name.ToLowerInvariant().Contains(normalizedQuery))
            {
                results.Add(BuildRecipeNode(recipe));
            }
        }

        return results;
    }

    /// <summary>
    /// Fully resolves a recipe tree and produces the ordered gather/craft lists.
    /// </summary>
    public ResolvedRecipe Resolve(RecipeNode rootRecipe, int quantity)
    {
        var gatherMap = new Dictionary<uint, MaterialRequirement>();
        var craftOrder = new List<CraftStep>();
        var otherMap = new Dictionary<uint, MaterialRequirement>();
        var visited = new HashSet<uint>();

        ResolveRecursive(rootRecipe, quantity, gatherMap, craftOrder, otherMap, visited);

        return new ResolvedRecipe
        {
            RootRecipe = rootRecipe,
            GatherList = gatherMap.Values.ToList(),
            CraftOrder = craftOrder,
            OtherMaterials = otherMap.Values.ToList(),
        };
    }

    private void ResolveRecursive(
        RecipeNode node,
        int quantity,
        Dictionary<uint, MaterialRequirement> gatherMap,
        List<CraftStep> craftOrder,
        Dictionary<uint, MaterialRequirement> otherMap,
        HashSet<uint> visited)
    {
        // How many times we need to craft this recipe
        var craftsNeeded = (int)Math.Ceiling((double)quantity / node.YieldPerCraft);

        foreach (var ingredient in node.Ingredients)
        {
            var totalNeeded = ingredient.QuantityNeeded * craftsNeeded;

            if (ingredient.IsCraftable && ingredient.RecipeId.HasValue)
            {
                // This ingredient is itself craftable -- recurse
                var subNode = node.SubRecipes.FirstOrDefault(s => s.RecipeId == ingredient.RecipeId.Value);
                if (subNode != null && !visited.Contains(subNode.RecipeId))
                {
                    visited.Add(subNode.RecipeId);
                    ResolveRecursive(subNode, totalNeeded, gatherMap, craftOrder, otherMap, visited);
                }
            }
            else if (ingredient.IsGatherable)
            {
                // Raw gatherable material
                if (gatherMap.TryGetValue(ingredient.ItemId, out var existing))
                {
                    existing.QuantityNeeded += totalNeeded;
                }
                else
                {
                    gatherMap[ingredient.ItemId] = new MaterialRequirement
                    {
                        ItemId = ingredient.ItemId,
                        ItemName = ingredient.ItemName,
                        QuantityNeeded = totalNeeded,
                        IsCraftable = false,
                        IsGatherable = true,
                        IsCollectable = ingredient.IsCollectable,
                        GatherType = ingredient.GatherType,
                    };
                }
            }
            else
            {
                // Not craftable, not gatherable (vendor, mob drop, etc.)
                if (otherMap.TryGetValue(ingredient.ItemId, out var existing))
                {
                    existing.QuantityNeeded += totalNeeded;
                }
                else
                {
                    otherMap[ingredient.ItemId] = new MaterialRequirement
                    {
                        ItemId = ingredient.ItemId,
                        ItemName = ingredient.ItemName,
                        QuantityNeeded = totalNeeded,
                        IsCraftable = false,
                        IsGatherable = false,
                        IsCollectable = false,
                    };
                }
            }
        }

        // Add this recipe's craft step (sub-components come before this in the list)
        craftOrder.Add(new CraftStep
        {
            Recipe = node,
            Quantity = craftsNeeded,
        });
    }

    private RecipeNode BuildRecipeNode(Recipe recipe)
    {
        var resultItem = recipe.ItemResult.Value;
        var ingredients = new List<MaterialRequirement>();
        var subRecipes = new List<RecipeNode>();

        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            var ingredientItem = recipe.Ingredient[i];
            var amount = recipe.AmountIngredient[i];

            if (ingredientItem.RowId == 0 || amount == 0) continue;

            var ingredientItemRow = itemSheet.GetRowOrDefault(ingredientItem.RowId);
            if (ingredientItemRow == null) continue;

            var itemName = ingredientItemRow.Value.Name.ExtractText();
            var isCraftable = recipesByItemId.ContainsKey(ingredientItem.RowId);
            var gatherType = GetGatherType(ingredientItem.RowId);
            var isGatherable = gatherType != GatherType.None;

            uint? subRecipeId = null;
            if (isCraftable && recipesByItemId.TryGetValue(ingredientItem.RowId, out var subRecipeList))
            {
                var subRecipeData = subRecipeList[0]; // Use first available recipe
                subRecipeId = subRecipeData.RowId;

                // Recursively build sub-recipe tree
                var subNode = BuildRecipeNode(subRecipeData);
                subRecipes.Add(subNode);
            }

            ingredients.Add(new MaterialRequirement
            {
                ItemId = ingredientItem.RowId,
                ItemName = itemName,
                QuantityNeeded = amount,
                IsCraftable = isCraftable,
                IsGatherable = isGatherable,
                IsCollectable = ingredientItemRow.Value.IsCollectable,
                RecipeId = subRecipeId,
                GatherType = gatherType,
            });
        }

        return new RecipeNode
        {
            ItemId = resultItem.RowId,
            ItemName = resultItem.Name.ExtractText(),
            RecipeId = recipe.RowId,
            YieldPerCraft = Math.Max(1, (int)recipe.AmountResult),
            CraftTypeId = (int)recipe.CraftType.RowId,
            RequiredLevel = recipe.RecipeLevelTable.Value.ClassJobLevel,
            IsCollectable = resultItem.IsCollectable,
            IsExpert = recipe.IsExpert,
            Ingredients = ingredients,
            SubRecipes = subRecipes,
        };
    }

    private GatherType GetGatherType(uint itemId)
    {
        foreach (var gi in gatheringItemSheet)
        {
            if (gi.Item.RowId == itemId)
            {
                // GatheringItem exists for this item. Determine type from
                // the parent GatheringPointBase -> GatheringType if available,
                // but for simplicity we mark as gatherable and let GBR figure it out.
                return GatherType.Unknown;
            }
        }
        return GatherType.None;
    }

    /// <summary>
    /// Returns the display name of a craft type ID.
    /// </summary>
    public static string GetCraftTypeName(int craftTypeId)
    {
        return craftTypeId switch
        {
            0 => "CRP",
            1 => "BSM",
            2 => "ARM",
            3 => "GSM",
            4 => "LTW",
            5 => "WVR",
            6 => "ALC",
            7 => "CUL",
            _ => "???",
        };
    }
}
