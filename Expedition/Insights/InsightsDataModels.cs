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

    // Price stats
    public float CurrentAveragePrice { get; init; }
    public float CurrentAveragePriceNQ { get; init; }
    public float CurrentAveragePriceHQ { get; init; }
    public float MinPrice { get; init; }
    public float MaxPrice { get; init; }

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

    // Status
    public bool IsLoading { get; init; }
    public string? ErrorMessage { get; init; }
}
