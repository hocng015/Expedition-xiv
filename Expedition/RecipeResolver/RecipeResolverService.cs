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
    private readonly VendorLookupService vendorLookup;
    private readonly MobDropLookupService mobDropLookup;

    // Cache: ItemId -> list of recipes that produce it
    private readonly Dictionary<uint, List<Recipe>> recipesByItemId = new();

    // Cache: GatheringItem RowId -> gather type + level
    private readonly Dictionary<uint, (GatherType Type, int Level)> gatherItemInfo = new();

    /// <summary>Provides vendor lookup for items.</summary>
    public VendorLookupService VendorLookup => vendorLookup;

    /// <summary>Provides mob drop lookup for items via Garland Tools.</summary>
    public MobDropLookupService MobDropLookup => mobDropLookup;

    public RecipeResolverService()
    {
        recipeSheet = DalamudApi.DataManager.GetExcelSheet<Recipe>()!;
        itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>()!;
        gatheringItemSheet = DalamudApi.DataManager.GetExcelSheet<GatheringItem>()!;

        BuildRecipeCache();
        BuildGatherCache();

        var zoneResolver = new GarlandZoneResolver();
        vendorLookup = new VendorLookupService(zoneResolver);
        mobDropLookup = new MobDropLookupService(zoneResolver);
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
    /// Browses recipes with filtering by craft class, level range, and special properties.
    /// </summary>
    public List<RecipeNode> BrowseRecipes(
        int? craftTypeId = null,
        int minLevel = 1,
        int maxLevel = 100,
        bool collectableOnly = false,
        bool expertOnly = false,
        bool specialistOnly = false,
        bool masterBookOnly = false)
    {
        var results = new List<RecipeNode>();

        foreach (var recipe in recipeSheet)
        {

            var resultItem = recipe.ItemResult.Value;
            if (resultItem.RowId == 0) continue;

            var name = resultItem.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            // Filter by craft class (cheap check first)
            if (craftTypeId.HasValue && (int)recipe.CraftType.RowId != craftTypeId.Value)
                continue;

            // Filter by level (cheap check)
            var level = recipe.RecipeLevelTable.Value.ClassJobLevel;
            if (level < minLevel || level > maxLevel)
                continue;

            // Filter by boolean flags (need to check item/recipe data)
            if (collectableOnly && !resultItem.IsCollectable)
                continue;

            if (expertOnly && !recipe.IsExpert)
                continue;

            if (specialistOnly && !recipe.IsSpecializationRequired)
                continue;

            if (masterBookOnly && recipe.SecretRecipeBook.RowId == 0)
                continue;

            results.Add(BuildRecipeNode(recipe));
        }

        return results;
    }

    /// <summary>
    /// Fully resolves a recipe tree and produces the ordered gather/craft lists.
    /// </summary>
    public ResolvedRecipe Resolve(RecipeNode rootRecipe, int quantity)
    {
        return Resolve(rootRecipe, quantity, inventoryLookup: null);
    }

    /// <summary>
    /// Fully resolves a recipe tree with inventory awareness.
    /// When inventoryLookup is provided, already-owned intermediate crafted items are deducted
    /// before recursing into sub-recipes, so only the delta of raw materials is computed.
    /// </summary>
    public ResolvedRecipe Resolve(RecipeNode rootRecipe, int quantity, Func<uint, int>? inventoryLookup)
    {
        var gatherMap = new Dictionary<uint, MaterialRequirement>();
        var craftOrder = new List<CraftStep>();
        var otherMap = new Dictionary<uint, MaterialRequirement>();
        var visited = new HashSet<uint>();

        ResolveRecursive(rootRecipe, quantity, gatherMap, craftOrder, otherMap, visited, inventoryLookup);

        var resolved = new ResolvedRecipe
        {
            RootRecipe = rootRecipe,
            GatherList = gatherMap.Values.ToList(),
            CraftOrder = craftOrder,
            OtherMaterials = otherMap.Values.ToList(),
        };

        // Enrich non-vendor "other" materials with mob drop data from Garland Tools
        EnrichMobDropData(resolved.OtherMaterials);

        return resolved;
    }

    /// <summary>
    /// Pre-fetches and populates mob drop information for non-vendor materials.
    /// Uses MobDropLookupService (Garland Tools API) with caching.
    /// </summary>
    private void EnrichMobDropData(List<MaterialRequirement> otherMaterials)
    {
        // Collect item IDs for non-vendor items that might have mob drops
        var candidates = otherMaterials
            .Where(m => !m.IsVendorItem || m.VendorInfo == null)
            .ToList();

        if (candidates.Count == 0) return;

        // Pre-fetch all at once for concurrency
        try
        {
            mobDropLookup.PreFetch(candidates.Select(m => m.ItemId));
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "Failed to pre-fetch mob drop data");
        }

        // Populate each candidate
        foreach (var mat in candidates)
        {
            var drops = mobDropLookup.GetMobDrops(mat.ItemId);
            if (drops != null && drops.Count > 0)
            {
                mat.IsMobDrop = true;
                mat.MobDrops = drops;
            }
        }

        // Enrich mob coordinates from the dedicated Garland mob endpoint
        var allMobs = candidates
            .Where(m => m.MobDrops != null)
            .SelectMany(m => m.MobDrops!)
            .ToList();

        if (allMobs.Count > 0)
        {
            try
            {
                mobDropLookup.EnrichMobCoords(allMobs);
            }
            catch (Exception ex)
            {
                DalamudApi.Log.Warning(ex, "Failed to enrich mob drop coordinates");
            }
        }
    }

    private void ResolveRecursive(
        RecipeNode node,
        int quantity,
        Dictionary<uint, MaterialRequirement> gatherMap,
        List<CraftStep> craftOrder,
        Dictionary<uint, MaterialRequirement> otherMap,
        HashSet<uint> visited,
        Func<uint, int>? inventoryLookup)
    {
        // How many times we need to craft this recipe
        var craftsNeeded = (int)Math.Ceiling((double)quantity / node.YieldPerCraft);

        foreach (var ingredient in node.Ingredients)
        {
            var totalNeeded = ingredient.QuantityNeeded * craftsNeeded;

            if (ingredient.IsCraftable && ingredient.RecipeId.HasValue)
            {
                // This ingredient is itself craftable -- check inventory and deduct before recursing
                var actualNeeded = totalNeeded;
                if (inventoryLookup != null)
                {
                    var owned = inventoryLookup(ingredient.ItemId);
                    actualNeeded = Math.Max(0, totalNeeded - owned);
                }

                if (actualNeeded > 0)
                {
                    var subNode = node.SubRecipes.FirstOrDefault(s => s.RecipeId == ingredient.RecipeId.Value);
                    if (subNode != null && !visited.Contains(subNode.RecipeId))
                    {
                        visited.Add(subNode.RecipeId);
                        ResolveRecursive(subNode, actualNeeded, gatherMap, craftOrder, otherMap, visited, inventoryLookup);
                    }
                }
                // If actualNeeded == 0, the player already has enough -- skip entire sub-tree
            }
            else if (ingredient.IsGatherable)
            {
                // Raw gatherable material (leaf-level deduction handled by QuantityOwned post-resolve)
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
                        IconId = ingredient.IconId,
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
                    var vendorInfo = vendorLookup.GetVendorInfo(ingredient.ItemId);
                    otherMap[ingredient.ItemId] = new MaterialRequirement
                    {
                        ItemId = ingredient.ItemId,
                        ItemName = ingredient.ItemName,
                        IconId = ingredient.IconId,
                        QuantityNeeded = totalNeeded,
                        IsCraftable = false,
                        IsGatherable = false,
                        IsCollectable = false,
                        IsVendorItem = vendorInfo != null,
                        VendorInfo = vendorInfo,
                    };
                }
            }
        }

        // Add this recipe's craft step (sub-components come before this in the list)
        if (craftsNeeded > 0)
        {
            craftOrder.Add(new CraftStep
            {
                Recipe = node,
                Quantity = craftsNeeded,
            });
        }
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

            var isCrystal = IsCrystalItem(itemName);
            var isAethersand = Gathering.AetherialReductionManager.IsAethersandItem(itemName);

            ingredients.Add(new MaterialRequirement
            {
                ItemId = ingredientItem.RowId,
                ItemName = itemName,
                IconId = (uint)ingredientItemRow.Value.Icon,
                QuantityNeeded = amount,
                IsCraftable = isCraftable,
                IsGatherable = isGatherable,
                IsCollectable = ingredientItemRow.Value.IsCollectable,
                RecipeId = subRecipeId,
                GatherType = gatherType,
                IsCrystal = isCrystal,
                IsAetherialReductionSource = isAethersand,
            });
        }

        var recipeLevelRow = recipe.RecipeLevelTable.Value;

        return new RecipeNode
        {
            ItemId = resultItem.RowId,
            ItemName = resultItem.Name.ExtractText(),
            IconId = (uint)resultItem.Icon,
            RecipeId = recipe.RowId,
            YieldPerCraft = Math.Max(1, (int)recipe.AmountResult),
            CraftTypeId = (int)recipe.CraftType.RowId,
            RequiredLevel = recipeLevelRow.ClassJobLevel,
            IsCollectable = resultItem.IsCollectable,
            IsExpert = recipe.IsExpert,
            RequiresSpecialist = recipe.IsSpecializationRequired,
            RecipeDurability = recipeLevelRow.Durability * recipe.DurabilityFactor / 100,
            SuggestedCraftsmanship = recipeLevelRow.SuggestedCraftsmanship,
            SuggestedControl = 0, // Field removed from RecipeLevelTable in latest Lumina
            RequiresMasterBook = recipe.SecretRecipeBook.RowId != 0,
            MasterBookId = recipe.SecretRecipeBook.RowId,
            Ingredients = ingredients,
            SubRecipes = subRecipes,
        };
    }

    private GatherType GetGatherType(uint itemId)
    {
        if (gatherItemInfo.TryGetValue(itemId, out var info))
            return info.Type;
        return GatherType.None;
    }

    private static bool IsCrystalItem(string name)
    {
        return name.Contains("Shard") || name.Contains("Crystal") || name.Contains("Cluster");
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

    /// <summary>
    /// Returns the display name for a gather type.
    /// </summary>
    public static string GetGatherTypeName(GatherType type)
    {
        return type switch
        {
            GatherType.Miner => "MIN",
            GatherType.Botanist => "BTN",
            GatherType.Fisher => "FSH",
            _ => "???",
        };
    }

    private void BuildGatherCache()
    {
        var gpbSheet = DalamudApi.DataManager.GetExcelSheet<GatheringPointBase>();
        if (gpbSheet == null) return;

        // Build mapping: GatheringItem RowId -> GatherType
        // GatheringType RowId: 0,1 = Mining, 2,3 = Botany, 4,5 = Fishing
        var gatherItemTypeMap = new Dictionary<uint, GatherType>();
        var gatherItemLevelMap = new Dictionary<uint, int>();

        foreach (var gpb in gpbSheet)
        {
            var gatherTypeId = gpb.GatheringType.RowId;
            var gatherType = gatherTypeId switch
            {
                0 or 1 => GatherType.Miner,
                2 or 3 => GatherType.Botanist,
                4 or 5 => GatherType.Fisher,
                _ => GatherType.Unknown,
            };
            if (gatherType == GatherType.Unknown) continue;

            var level = (int)gpb.GatheringLevel;

            for (var i = 0; i < gpb.Item.Count; i++)
            {
                var giRef = gpb.Item[i];
                if (giRef.RowId == 0) continue;

                if (!gatherItemTypeMap.ContainsKey(giRef.RowId))
                {
                    gatherItemTypeMap[giRef.RowId] = gatherType;
                    gatherItemLevelMap[giRef.RowId] = level;
                }
            }
        }

        // Map GatheringItem RowIds to actual Item IDs, but only if
        // we found the item in a real gathering point (has known type + level)
        foreach (var gi in gatheringItemSheet)
        {
            if (gi.Item.RowId == 0) continue;

            if (!gatherItemTypeMap.TryGetValue(gi.RowId, out var type)) continue;
            if (type == GatherType.Unknown) continue;

            var level = gatherItemLevelMap.GetValueOrDefault(gi.RowId, 0);
            if (level <= 0) continue;

            gatherItemInfo[gi.Item.RowId] = (type, level);
        }

        // Also add fishing items from FishingSpot sheet
        var fishingSpotSheet = DalamudApi.DataManager.GetExcelSheet<FishingSpot>();
        if (fishingSpotSheet != null)
        {
            foreach (var spot in fishingSpotSheet)
            {
                var level = (int)spot.GatheringLevel;
                if (level <= 0) continue;

                for (var i = 0; i < spot.Item.Count; i++)
                {
                    var itemRef = spot.Item[i];
                    if (itemRef.RowId == 0) continue;

                    if (!gatherItemInfo.ContainsKey(itemRef.RowId))
                        gatherItemInfo[itemRef.RowId] = (GatherType.Fisher, level);
                }
            }
        }

        // Also add spearfishing items from SpearfishingItem sheet
        var spearfishSheet = DalamudApi.DataManager.GetExcelSheet<SpearfishingItem>();
        if (spearfishSheet != null)
        {
            foreach (var sf in spearfishSheet)
            {
                var itemRef = sf.Item;
                if (itemRef.RowId == 0) continue;

                var sfLevel = (int)sf.GatheringItemLevel.RowId;
                if (sfLevel <= 0) continue;

                if (!gatherItemInfo.ContainsKey(itemRef.RowId))
                    gatherItemInfo[itemRef.RowId] = (GatherType.Fisher, sfLevel);
            }
        }
    }

    /// <summary>
    /// Browse gatherable items by gathering class and level range.
    /// </summary>
    public List<GatherableItemInfo> BrowseGatherItems(
        GatherType? gatherClass = null,
        int minLevel = 1,
        int maxLevel = 100,
        bool collectableOnly = false,
        bool hideCrystals = true,
        int maxResults = 200)
    {
        var results = new List<GatherableItemInfo>();

        foreach (var kvp in gatherItemInfo)
        {
            if (results.Count >= maxResults) break;

            var itemId = kvp.Key;
            var (type, level) = kvp.Value;

            // Skip items without a known gather class
            if (type == GatherType.Unknown || type == GatherType.None)
                continue;

            // Filter by gather class
            if (gatherClass.HasValue && type != gatherClass.Value)
                continue;

            // Filter by level
            if (level < minLevel || level > maxLevel)
                continue;

            // Look up item data
            var itemRow = itemSheet.GetRowOrDefault(itemId);
            if (itemRow == null) continue;

            var name = itemRow.Value.Name.ExtractText();
            if (string.IsNullOrEmpty(name)) continue;

            var isCrystal = IsCrystalItem(name);
            if (hideCrystals && isCrystal)
                continue;

            if (collectableOnly && !itemRow.Value.IsCollectable)
                continue;

            var isAlsoCraftable = recipesByItemId.ContainsKey(itemId);

            results.Add(new GatherableItemInfo
            {
                ItemId = itemId,
                ItemName = name,
                IconId = (uint)itemRow.Value.Icon,
                GatherClass = type,
                GatherLevel = level,
                IsCollectable = itemRow.Value.IsCollectable,
                IsCrystal = isCrystal,
                IsAlsoCraftable = isAlsoCraftable,
                ItemLevel = itemRow.Value.LevelItem.RowId > 0 ? (int)itemRow.Value.LevelItem.RowId : 0,
            });
        }

        // Sort by level then name
        results.Sort((a, b) =>
        {
            var levelCmp = a.GatherLevel.CompareTo(b.GatherLevel);
            return levelCmp != 0 ? levelCmp : string.Compare(a.ItemName, b.ItemName, StringComparison.Ordinal);
        });

        if (results.Count > maxResults)
            results.RemoveRange(maxResults, results.Count - maxResults);

        return results;
    }
}
