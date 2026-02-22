using Lumina.Excel.Sheets;

namespace Expedition.Diadem;

/// <summary>
/// Gathering class for Diadem items (Miner or Botanist).
/// </summary>
public enum DiademGatherClass { Miner, Botanist }

/// <summary>
/// Node tier in the Diadem. Determines actual node level and XP yield.
/// </summary>
public enum DiademNodeTier
{
    /// <summary>Tier 1 nodes (displayed Lv10). Best for leveling 1-59.</summary>
    Tier1,

    /// <summary>Tier 2 nodes (displayed Lv60). Requires Lv60+. Best for leveling 60-79.</summary>
    Tier2,

    /// <summary>Tier 3 nodes (Lv80 standard). Requires Lv80+. Best for leveling 80-89.</summary>
    Tier3,

    /// <summary>Expert nodes (Lv80 artisanal collectables). For 80+.</summary>
    Expert,

    /// <summary>Umbral weather nodes. Special timed spawns at Lv80.</summary>
    Umbral,
}

/// <summary>
/// A single Diadem-gatherable item with metadata for display and recommendations.
/// </summary>
public sealed class DiademItem
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint IconId { get; set; }
    public DiademGatherClass GatherClass { get; init; }
    public DiademNodeTier NodeTier { get; init; }

    /// <summary>Minimum player level for this tier to give good XP.</summary>
    public int RecommendedMinLevel { get; init; }

    /// <summary>Level above which this tier becomes suboptimal XP.</summary>
    public int RecommendedMaxLevel { get; init; }

    /// <summary>If true, this is a collectable from an expert node.</summary>
    public bool IsExpert { get; init; }

    /// <summary>Weather condition required (null if always available).</summary>
    public string? WeatherCondition { get; init; }

    /// <summary>Sub-type: Mining/Quarrying for MIN, Logging/Harvesting for BTN.</summary>
    public string NodeType { get; init; } = string.Empty;
}

/// <summary>
/// Static database of all Diadem-gatherable items. Validated against Lumina at init time.
/// </summary>
public static class DiademItemDatabase
{
    private static bool initialized;
    private static readonly List<DiademItem> items = new();

    public static IReadOnlyList<DiademItem> AllItems => items;

    /// <summary>
    /// Initializes the database, validating item IDs and populating icons from Lumina.
    /// </summary>
    public static void Initialize()
    {
        if (initialized) return;

        BuildItemList();

        // Validate against Lumina and populate icons
        var itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>();
        if (itemSheet != null)
        {
            foreach (var item in items)
            {
                try
                {
                    var row = itemSheet.GetRow(item.ItemId);
                    item.IconId = row.Icon;

                    // Use Lumina name if available (more accurate than hardcoded)
                    var luminaName = row.Name.ToString();
                    if (!string.IsNullOrEmpty(luminaName) && luminaName != item.Name)
                    {
                        DalamudApi.Log.Debug($"[Diadem] Item {item.ItemId}: name updated '{item.Name}' -> '{luminaName}'");
                    }
                }
                catch
                {
                    DalamudApi.Log.Warning($"[Diadem] Item {item.ItemId} ({item.Name}) not found in Lumina Item sheet.");
                }
            }
        }

        initialized = true;
        DalamudApi.Log.Information($"[Diadem] Item database initialized: {items.Count} items.");
    }

    public static IEnumerable<DiademItem> GetItemsForClass(DiademGatherClass gatherClass)
        => items.Where(i => i.GatherClass == gatherClass);

    public static IEnumerable<DiademItem> GetItemsByTier(DiademNodeTier tier)
        => items.Where(i => i.NodeTier == tier);

    /// <summary>
    /// Returns items recommended for the player's current level.
    /// Sorted by priority (best XP first).
    /// </summary>
    public static IReadOnlyList<DiademItem> GetRecommendedItems(DiademGatherClass gatherClass, int playerLevel)
    {
        return items
            .Where(i => i.GatherClass == gatherClass
                        && i.WeatherCondition == null  // Exclude weather-dependent
                        && playerLevel >= i.RecommendedMinLevel
                        && playerLevel <= i.RecommendedMaxLevel)
            .OrderByDescending(i => (int)i.NodeTier) // Higher tier = more XP
            .ToList();
    }

    /// <summary>
    /// Returns the display name for a node tier.
    /// </summary>
    public static string GetTierDisplayName(DiademNodeTier tier) => tier switch
    {
        DiademNodeTier.Tier1 => "Tier 1 (Lv10 Nodes)",
        DiademNodeTier.Tier2 => "Tier 2 (Lv60 Nodes)",
        DiademNodeTier.Tier3 => "Tier 3 (Lv80 Nodes)",
        DiademNodeTier.Expert => "Expert (Lv80 Artisanal)",
        DiademNodeTier.Umbral => "Umbral Weather",
        _ => tier.ToString(),
    };

    /// <summary>
    /// Returns the recommended level range text for a tier.
    /// </summary>
    public static string GetTierLevelRange(DiademNodeTier tier) => tier switch
    {
        DiademNodeTier.Tier1 => "Lv 1-59",
        DiademNodeTier.Tier2 => "Lv 60-79",
        DiademNodeTier.Tier3 => "Lv 80-89",
        DiademNodeTier.Expert => "Lv 80+",
        DiademNodeTier.Umbral => "Lv 80+",
        _ => "",
    };

    /// <summary>
    /// Returns the approximate minimum Gathering stat needed for a 95%+ success rate
    /// on nodes of the given tier. These are rough thresholds for guidance.
    /// </summary>
    public static int GetMinGatheringForTier(DiademNodeTier tier) => tier switch
    {
        DiademNodeTier.Tier1 => 0,     // Starter gear works fine
        DiademNodeTier.Tier2 => 600,   // Need ~Lv60+ gathering gear
        DiademNodeTier.Tier3 => 2000,  // Need ~Lv75-80 gathering gear
        DiademNodeTier.Expert => 2200, // Need endgame Lv80+ gathering gear
        DiademNodeTier.Umbral => 2200, // Same as expert
        _ => 0,
    };

    /// <summary>
    /// Returns a gear guidance tip for a tier, explaining where to get adequate gear.
    /// </summary>
    public static string GetGearTip(DiademNodeTier tier) => tier switch
    {
        DiademNodeTier.Tier1 =>
            "Any gear works. Starter gathering gear is sufficient for Tier 1 nodes.",
        DiademNodeTier.Tier2 =>
            "Equip Lv60+ gathering gear. Buy from Ishgard vendors, the Market Board, or craft HQ gear. " +
            "Aim for 600+ Gathering stat.",
        DiademNodeTier.Tier3 =>
            "Equip Lv80 gathering gear. Purchase Facet/Aesthete's gear from the Market Board or craft it. " +
            "Aim for 2000+ Gathering stat.",
        DiademNodeTier.Expert =>
            "Equip best-in-slot Lv80+ gathering gear with melds. " +
            "Aesthete's or higher with materia. Aim for 2200+ Gathering stat.",
        DiademNodeTier.Umbral =>
            "Same gear as Expert nodes — best-in-slot Lv80+ gathering gear with materia melds.",
        _ => "",
    };

    /// <summary>
    /// Returns the recommended tier for a given player level.
    /// </summary>
    public static DiademNodeTier GetRecommendedTier(int playerLevel)
    {
        if (playerLevel >= 80) return DiademNodeTier.Tier3;
        if (playerLevel >= 60) return DiademNodeTier.Tier2;
        return DiademNodeTier.Tier1;
    }

    private static void BuildItemList()
    {
        items.Clear();

        // ═══════════════════════════════════════════════
        // TIER 1 — Lv10 Nodes (good for leveling 1-59)
        // ═══════════════════════════════════════════════

        // Miner — Mining
        AddItem(32012, "Grade 4 Skybuilders' Ore", DiademGatherClass.Miner, DiademNodeTier.Tier1, "Mining", 1, 59);
        AddItem(32007, "Grade 4 Skybuilders' Iron Ore", DiademGatherClass.Miner, DiademNodeTier.Tier1, "Mining", 1, 59);
        // Miner — Quarrying
        AddItem(32008, "Grade 4 Skybuilders' Iron Sand", DiademGatherClass.Miner, DiademNodeTier.Tier1, "Quarrying", 1, 59);

        // Botanist — Logging
        AddItem(32005, "Grade 4 Skybuilders' Switch", DiademGatherClass.Botanist, DiademNodeTier.Tier1, "Logging", 1, 59);
        AddItem(32009, "Grade 4 Skybuilders' Mahogany Log", DiademGatherClass.Botanist, DiademNodeTier.Tier1, "Logging", 1, 59);
        // Botanist — Harvesting
        AddItem(32006, "Grade 4 Skybuilders' Hemp", DiademGatherClass.Botanist, DiademNodeTier.Tier1, "Harvesting", 1, 59);
        AddItem(32010, "Grade 4 Skybuilders' Sesame", DiademGatherClass.Botanist, DiademNodeTier.Tier1, "Harvesting", 1, 59);

        // ═══════════════════════════════════════════════
        // TIER 2 — Lv60 Nodes (requires Lv60+, good for leveling 60-79)
        // ═══════════════════════════════════════════════

        // Miner — Mining
        AddItem(32013, "Grade 4 Skybuilders' Rock Salt", DiademGatherClass.Miner, DiademNodeTier.Tier2, "Mining", 60, 79);
        AddItem(32020, "Grade 4 Skybuilders' Electrum Ore", DiademGatherClass.Miner, DiademNodeTier.Tier2, "Mining", 60, 79);
        AddItem(32021, "Grade 4 Skybuilders' Alumen", DiademGatherClass.Miner, DiademNodeTier.Tier2, "Mining", 60, 79);
        AddItem(32022, "Grade 4 Skybuilders' Spring Water", DiademGatherClass.Miner, DiademNodeTier.Tier2, "Mining", 60, 79);
        // Miner — Quarrying
        AddItem(32014, "Grade 4 Skybuilders' Mythrite Sand", DiademGatherClass.Miner, DiademNodeTier.Tier2, "Quarrying", 60, 79);
        AddItem(32023, "Grade 4 Skybuilders' Gold Sand", DiademGatherClass.Miner, DiademNodeTier.Tier2, "Quarrying", 60, 79);

        // Botanist — Logging
        AddItem(32015, "Grade 4 Skybuilders' Spruce Log", DiademGatherClass.Botanist, DiademNodeTier.Tier2, "Logging", 60, 79);
        AddItem(32016, "Grade 4 Skybuilders' Mistletoe", DiademGatherClass.Botanist, DiademNodeTier.Tier2, "Logging", 60, 79);
        // Botanist — Harvesting
        AddItem(32011, "Grade 4 Skybuilders' Cotton Boll", DiademGatherClass.Botanist, DiademNodeTier.Tier2, "Harvesting", 60, 79);
        AddItem(32017, "Grade 4 Skybuilders' Toad", DiademGatherClass.Botanist, DiademNodeTier.Tier2, "Harvesting", 60, 79);
        AddItem(32018, "Grade 4 Skybuilders' Vine", DiademGatherClass.Botanist, DiademNodeTier.Tier2, "Harvesting", 60, 79);
        AddItem(32019, "Grade 4 Skybuilders' Tea Leaves", DiademGatherClass.Botanist, DiademNodeTier.Tier2, "Harvesting", 60, 79);

        // ═══════════════════════════════════════════════
        // TIER 3 — Lv80 Nodes (requires Lv80+, good for leveling 80-89)
        // ═══════════════════════════════════════════════

        // Miner — Mining
        AddItem(32031, "Grade 4 Skybuilders' Finest Rock Salt", DiademGatherClass.Miner, DiademNodeTier.Tier3, "Mining", 80, 89);
        AddItem(32030, "Grade 4 Skybuilders' Gold Ore", DiademGatherClass.Miner, DiademNodeTier.Tier3, "Mining", 80, 89);
        AddItem(32032, "Grade 4 Skybuilders' Truespring Water", DiademGatherClass.Miner, DiademNodeTier.Tier3, "Mining", 80, 89);
        // Miner — Quarrying
        AddItem(32024, "Grade 4 Skybuilders' Ragstone", DiademGatherClass.Miner, DiademNodeTier.Tier3, "Quarrying", 80, 89);
        AddItem(32034, "Grade 4 Skybuilders' Bluespirit Ore", DiademGatherClass.Miner, DiademNodeTier.Tier3, "Quarrying", 80, 89);
        AddItem(32033, "Grade 4 Skybuilders' Mineral Sand", DiademGatherClass.Miner, DiademNodeTier.Tier3, "Quarrying", 80, 89);

        // Botanist — Logging
        AddItem(32025, "Grade 4 Skybuilders' White Cedar Log", DiademGatherClass.Botanist, DiademNodeTier.Tier3, "Logging", 80, 89);
        AddItem(32026, "Grade 4 Skybuilders' Primordial Resin", DiademGatherClass.Botanist, DiademNodeTier.Tier3, "Logging", 80, 89);
        // Botanist — Harvesting
        AddItem(32028, "Grade 4 Skybuilders' Gossamer Cotton Boll", DiademGatherClass.Botanist, DiademNodeTier.Tier3, "Harvesting", 80, 89);
        AddItem(32029, "Grade 4 Skybuilders' Tortoise", DiademGatherClass.Botanist, DiademNodeTier.Tier3, "Harvesting", 80, 89);
        AddItem(32027, "Grade 4 Skybuilders' Wheat", DiademGatherClass.Botanist, DiademNodeTier.Tier3, "Harvesting", 80, 89);

        // ═══════════════════════════════════════════════
        // EXPERT — Lv80 Artisanal Nodes (for 80+)
        // ═══════════════════════════════════════════════

        // Grade 4 Artisanal — Miner
        AddExpert(32040, "Grade 4 Artisanal Skybuilders' Cloudstone", DiademGatherClass.Miner, "Mining");
        AddExpert(32041, "Grade 4 Artisanal Skybuilders' Spring Water", DiademGatherClass.Miner, "Mining");
        AddExpert(32042, "Grade 4 Artisanal Skybuilders' Ice Stalagmite", DiademGatherClass.Miner, "Mining");
        AddExpert(32043, "Grade 4 Artisanal Skybuilders' Silex", DiademGatherClass.Miner, "Quarrying");
        AddExpert(32044, "Grade 4 Artisanal Skybuilders' Prismstone", DiademGatherClass.Miner, "Quarrying");

        // Grade 4 Artisanal — Botanist
        AddExpert(32035, "Grade 4 Artisanal Skybuilders' Log", DiademGatherClass.Botanist, "Logging");
        AddExpert(32036, "Grade 4 Artisanal Skybuilders' Raspberry", DiademGatherClass.Botanist, "Logging");
        AddExpert(32037, "Grade 4 Artisanal Skybuilders' Caiman", DiademGatherClass.Botanist, "Harvesting");
        AddExpert(32038, "Grade 4 Artisanal Skybuilders' Cocoon", DiademGatherClass.Botanist, "Harvesting");
        AddExpert(32039, "Grade 4 Artisanal Skybuilders' Barbgrass", DiademGatherClass.Botanist, "Harvesting");

        // Grade 2 Artisanal — Miner
        AddExpert(29939, "Grade 2 Artisanal Skybuilders' Cloudstone", DiademGatherClass.Miner, "Mining");
        AddExpert(29940, "Grade 2 Artisanal Skybuilders' Rock Salt", DiademGatherClass.Miner, "Mining");
        AddExpert(29941, "Grade 2 Artisanal Skybuilders' Spring Water", DiademGatherClass.Miner, "Mining");
        AddExpert(29943, "Grade 2 Artisanal Skybuilders' Jade", DiademGatherClass.Miner, "Quarrying");
        AddExpert(29942, "Grade 2 Artisanal Skybuilders' Aurum Regis Sand", DiademGatherClass.Miner, "Quarrying");

        // Grade 2 Artisanal — Botanist
        AddExpert(29934, "Grade 2 Artisanal Skybuilders' Log", DiademGatherClass.Botanist, "Logging");
        AddExpert(29935, "Grade 2 Artisanal Skybuilders' Hardened Sap", DiademGatherClass.Botanist, "Logging");
        AddExpert(29937, "Grade 2 Artisanal Skybuilders' Cotton Boll", DiademGatherClass.Botanist, "Harvesting");
        AddExpert(29936, "Grade 2 Artisanal Skybuilders' Wheat", DiademGatherClass.Botanist, "Harvesting");
        AddExpert(29938, "Grade 2 Artisanal Skybuilders' Dawn Lizard", DiademGatherClass.Botanist, "Harvesting");

        // Grade 3 Artisanal — Miner
        AddExpert(31311, "Grade 3 Artisanal Skybuilders' Cloudstone", DiademGatherClass.Miner, "Mining");
        AddExpert(31312, "Grade 3 Artisanal Skybuilders' Basilisk Egg", DiademGatherClass.Miner, "Mining");
        AddExpert(31313, "Grade 3 Artisanal Skybuilders' Alumen", DiademGatherClass.Miner, "Mining");
        AddExpert(31315, "Grade 3 Artisanal Skybuilders' Granite", DiademGatherClass.Miner, "Quarrying");
        AddExpert(31314, "Grade 3 Artisanal Skybuilders' Clay", DiademGatherClass.Miner, "Quarrying");

        // Grade 3 Artisanal — Botanist
        AddExpert(31306, "Grade 3 Artisanal Skybuilders' Log", DiademGatherClass.Botanist, "Logging");
        AddExpert(31307, "Grade 3 Artisanal Skybuilders' Amber", DiademGatherClass.Botanist, "Logging");
        AddExpert(31308, "Grade 3 Artisanal Skybuilders' Cotton Boll", DiademGatherClass.Botanist, "Harvesting");
        AddExpert(31309, "Grade 3 Artisanal Skybuilders' Rice", DiademGatherClass.Botanist, "Harvesting");
        AddExpert(31310, "Grade 3 Artisanal Skybuilders' Vine", DiademGatherClass.Botanist, "Harvesting");

        // ═══════════════════════════════════════════════
        // UMBRAL — Weather-dependent special nodes
        // ═══════════════════════════════════════════════

        // Grade 4 Umbral
        AddUmbral(32047, "Grade 4 Skybuilders' Umbral Flarerock", DiademGatherClass.Miner, "Mining", "Umbral Flare");
        AddUmbral(32048, "Grade 4 Skybuilders' Umbral Levinsand", DiademGatherClass.Miner, "Quarrying", "Umbral Levin");
        AddUmbral(32046, "Grade 4 Skybuilders' Umbral Dirtleaf", DiademGatherClass.Botanist, "Logging", "Umbral Duststorm");
        AddUmbral(32045, "Grade 4 Skybuilders' Umbral Galewood Branch", DiademGatherClass.Botanist, "Harvesting", "Umbral Tempest");

        // Grade 2 Umbral
        AddUmbral(29946, "Grade 2 Skybuilders' Umbral Flarestone", DiademGatherClass.Miner, "Mining", "Umbral Flare");
        AddUmbral(29947, "Grade 2 Skybuilders' Umbral Levinshard", DiademGatherClass.Miner, "Quarrying", "Umbral Levin");
        AddUmbral(29945, "Grade 2 Skybuilders' Umbral Earthcap", DiademGatherClass.Botanist, "Logging", "Umbral Duststorm");
        AddUmbral(29944, "Grade 2 Skybuilders' Umbral Galewood Log", DiademGatherClass.Botanist, "Harvesting", "Umbral Tempest");

        // Grade 3 Umbral
        AddUmbral(31318, "Grade 3 Skybuilders' Umbral Magma Shard", DiademGatherClass.Miner, "Mining", "Umbral Flare");
        AddUmbral(31319, "Grade 3 Skybuilders' Umbral Levinite", DiademGatherClass.Miner, "Quarrying", "Umbral Levin");
        AddUmbral(31317, "Grade 3 Skybuilders' Umbral Tortoise", DiademGatherClass.Botanist, "Logging", "Umbral Duststorm");
        AddUmbral(31316, "Grade 3 Skybuilders' Umbral Galewood Sap", DiademGatherClass.Botanist, "Harvesting", "Umbral Tempest");
    }

    private static void AddItem(uint id, string name, DiademGatherClass cls, DiademNodeTier tier, string nodeType,
        int recMinLevel, int recMaxLevel)
    {
        items.Add(new DiademItem
        {
            ItemId = id,
            Name = name,
            GatherClass = cls,
            NodeTier = tier,
            NodeType = nodeType,
            RecommendedMinLevel = recMinLevel,
            RecommendedMaxLevel = recMaxLevel,
        });
    }

    private static void AddExpert(uint id, string name, DiademGatherClass cls, string nodeType)
    {
        items.Add(new DiademItem
        {
            ItemId = id,
            Name = name,
            GatherClass = cls,
            NodeTier = DiademNodeTier.Expert,
            NodeType = nodeType,
            RecommendedMinLevel = 80,
            RecommendedMaxLevel = 100,
            IsExpert = true,
        });
    }

    private static void AddUmbral(uint id, string name, DiademGatherClass cls, string nodeType, string weather)
    {
        items.Add(new DiademItem
        {
            ItemId = id,
            Name = name,
            GatherClass = cls,
            NodeTier = DiademNodeTier.Umbral,
            NodeType = nodeType,
            RecommendedMinLevel = 80,
            RecommendedMaxLevel = 100,
            IsExpert = true,
            WeatherCondition = weather,
        });
    }
}
