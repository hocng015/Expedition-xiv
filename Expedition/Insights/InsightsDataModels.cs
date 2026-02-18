namespace Expedition.Insights;

/// <summary>
/// Parsed response from Universalis GET /{dc}/{itemIds} endpoint.
/// Represents market data for a single item on a data center.
/// </summary>
public sealed class MarketItemData
{
    public uint ItemId { get; init; }
    public string ItemName { get; set; } = string.Empty;
    public uint IconId { get; set; }

    // Price stats — based on actual SALE history (immune to troll listings)
    public float CurrentAveragePrice { get; init; }
    public float CurrentAveragePriceNQ { get; init; }
    public float CurrentAveragePriceHQ { get; init; }
    public float MinPrice { get; init; }
    public float MaxPrice { get; init; }

    // Listing average — the mean of current MB listings. Kept for reference
    // but NOT used for display or ranking because troll listings at 999M
    // inflate it wildly (e.g. a 1.5K-gil meal shows as 23M).
    public float ListingAveragePrice { get; init; }

    // Velocity (units sold per day) — the key ranking metric
    public float RegularSaleVelocity { get; init; }
    public float NqSaleVelocity { get; init; }
    public float HqSaleVelocity { get; init; }

    // Supply info
    public int UnitsForSale { get; init; }
    public int UnitsSold { get; init; }
    public int ListingsCount { get; init; }

    // Per-world data for cross-DC comparison
    public List<WorldPriceSnapshot> WorldPrices { get; init; } = new();

    // Computed fields
    public float EstimatedDailyGilVolume => RegularSaleVelocity * CurrentAveragePrice;

    // Category tag (set by InsightsEngine during snapshot build)
    public string CategoryName { get; set; } = string.Empty;
}

/// <summary>
/// Per-world price snapshot extracted from Universalis listings.
/// </summary>
public sealed class WorldPriceSnapshot
{
    public string WorldName { get; init; } = string.Empty;
    public int WorldId { get; init; }
    public float MinPrice { get; init; }
    public int ListingCount { get; init; }
    public long LastUploadTime { get; init; }
}

/// <summary>
/// A recent purchase transaction from the Universalis history endpoint.
/// </summary>
public sealed class RecentSale
{
    public uint ItemId { get; init; }
    public string ItemName { get; set; } = string.Empty;
    public uint IconId { get; set; }
    public int PricePerUnit { get; init; }
    public int Quantity { get; init; }
    public long Total => (long)PricePerUnit * Quantity;
    public bool IsHq { get; init; }
    public string WorldName { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Activity data from the most-recently-updated endpoint.
/// </summary>
public sealed class RecentlyUpdatedItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; set; } = string.Empty;
    public uint IconId { get; set; }
    public string WorldName { get; init; } = string.Empty;
    public DateTime LastUploadTime { get; init; }
}

/// <summary>
/// Aggregated category summary for the overview section.
/// </summary>
public sealed class CategorySummary
{
    public string CategoryName { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public float TotalDailyVelocity { get; init; }
    public float AveragePrice { get; init; }
    public float EstimatedDailyGilVolume { get; init; }
    public MarketItemData? TopItem { get; init; }
}

/// <summary>
/// Complete snapshot of all insights data, atomically swapped on refresh.
/// Immutable once constructed — safe for lock-free reads from the UI thread.
/// </summary>
public sealed class InsightsSnapshot
{
    public DateTime FetchedAt { get; init; }
    public string DataCenterName { get; init; } = string.Empty;

    // Core ranked lists
    public List<MarketItemData> HottestItems { get; init; } = new();       // Top 50 by velocity
    public List<MarketItemData> HighestGilVolume { get; init; } = new();   // Top 50 by daily gil
    public List<MarketItemData> MostExpensive { get; init; } = new();      // Top 50 by avg price

    // Per-category breakdowns
    public List<CategorySummary> CategorySummaries { get; init; } = new();
    public Dictionary<string, List<MarketItemData>> ItemsByCategory { get; init; } = new();

    // Activity feed
    public List<RecentlyUpdatedItem> RecentActivity { get; init; } = new();

    // Saddlebag Exchange analytics
    public SaddlebagData? Saddlebag { get; init; }

    // Status
    public bool IsLoading { get; init; }
    public string? ErrorMessage { get; init; }
}

// ──────────────────────────────────────────────
// Saddlebag Exchange Data Models
// ──────────────────────────────────────────────

/// <summary>
/// Complete Saddlebag Exchange analytics bundle for a data center.
/// </summary>
public sealed class SaddlebagData
{
    public List<SaddlebagMarketShareItem> MarketShare { get; init; } = new();
    public List<SaddlebagCraftProfitItem> CraftProfit { get; init; } = new();
    public List<SaddlebagScripItem> ScripExchange { get; init; } = new();
    public List<SaddlebagWeeklyTrendItem> WeeklyTrends { get; init; } = new();
}

/// <summary>
/// Market share item from /api/ffxivmarketshare.
/// Includes price state: Crashing, Decreasing, Stable, Increasing, Spiking, Out of Stock.
/// </summary>
public sealed class SaddlebagMarketShareItem
{
    public string ItemName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public uint IconId { get; set; }
    public string State { get; init; } = string.Empty;
    public float AveragePrice { get; init; }
    public float MedianPrice { get; init; }
    public float MinPrice { get; init; }
    public float MarketValue { get; init; }
    public int QuantitySold { get; init; }
    public float PercentChange { get; init; }
    public float HomeMinPrice { get; init; }
    public float HomeMedianPrice { get; init; }
}

/// <summary>
/// Craft profit item from /api/v2/craftsim.
/// Shows crafting cost vs market revenue for profit analysis.
/// </summary>
public sealed class SaddlebagCraftProfitItem
{
    public string ItemName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public uint IconId { get; set; }
    public float Revenue { get; init; }
    public float CraftingCost { get; init; }
    public float Profit { get; init; }
    public float ProfitPercent { get; init; }
    public float AverageSold { get; init; }
    public float MedianPrice { get; init; }
    public float MinPrice { get; init; }
    public float HomeMinPrice { get; init; }
    public int SalesAmount { get; init; }
    public string Job { get; init; } = string.Empty;
}

/// <summary>
/// Scrip exchange item from /api/ffxiv/scripexchange.
/// Shows how much gil you get per scrip spent.
/// </summary>
public sealed class SaddlebagScripItem
{
    public string ItemName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public uint IconId { get; set; }
    public int ScripCost { get; init; }
    public float MarketPrice { get; init; }
    public float MinPrice { get; init; }
    public float HomeMinPrice { get; init; }
    public float GilPerScrip { get; init; }
    public int QuantitySold { get; init; }
}

/// <summary>
/// Weekly price trend item from /api/ffxiv/weekly-price-group-delta.
/// Shows price movement over the past week.
/// </summary>
public sealed class SaddlebagWeeklyTrendItem
{
    public string ItemName { get; init; } = string.Empty;
    public uint ItemId { get; init; }
    public uint IconId { get; set; }
    public float CurrentAverage { get; init; }
    public float PreviousAverage { get; init; }
    public float PriceDelta { get; init; }
    public float PercentChange { get; init; }
    public int SalesAmount { get; init; }
}
