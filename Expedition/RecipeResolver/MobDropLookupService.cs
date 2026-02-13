using System.Net.Http;
using System.Text.Json;

namespace Expedition.RecipeResolver;

/// <summary>
/// Looks up mob drop information for items via the Garland Tools API.
/// Results are cached to avoid repeated HTTP requests for the same item.
/// </summary>
public sealed class MobDropLookupService : IDisposable
{
    private const string GarlandItemUrl = "https://www.garlandtools.org/db/doc/item/en/3/{0}.json";
    private const string GarlandMobUrl = "https://www.garlandtools.org/db/doc/mob/en/2/{0}.json";
    private const int MaxMobsPerItem = 5; // Show top 5 lowest-level mobs
    private const int RequestTimeoutMs = 5000;

    private readonly HttpClient httpClient;
    private readonly Dictionary<uint, List<MobDropInfo>?> cache = new();

    // Zone ID -> zone name mapping from Garland Tools (populated as we encounter zones)
    private readonly Dictionary<int, string> zoneNameCache = new();

    // PlaceName RowId -> (TerritoryTypeId, MapId) for resolving Garland zone IDs to map data
    private readonly Dictionary<int, (uint TerritoryTypeId, uint MapId)> zoneToTerritoryMap = new();

    public MobDropLookupService()
    {
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs),
        };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Expedition-FFXIV-Plugin");

        BuildZoneNameCache();
        BuildZoneToTerritoryMap();
    }

    /// <summary>
    /// Returns mob drop info for the given item, or null if no mobs drop it.
    /// Uses a cache to avoid repeated API calls. Returns null on network errors.
    /// </summary>
    public List<MobDropInfo>? GetMobDrops(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var cached))
            return cached;

        // Synchronous fetch (called from UI thread context during preview)
        try
        {
            var result = FetchMobDropsAsync(itemId).GetAwaiter().GetResult();
            cache[itemId] = result;
            return result;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, $"Failed to fetch mob drop data for item {itemId}");
            cache[itemId] = null;
            return null;
        }
    }

    /// <summary>
    /// Pre-fetches mob drop data for multiple items concurrently.
    /// Call this during recipe resolution to warm the cache.
    /// </summary>
    public void PreFetch(IEnumerable<uint> itemIds)
    {
        var toFetch = itemIds.Where(id => !cache.ContainsKey(id)).ToList();
        if (toFetch.Count == 0) return;

        try
        {
            var tasks = toFetch.Select(id => FetchAndCacheAsync(id)).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "Failed to pre-fetch mob drop data");
        }
    }

    private async Task FetchAndCacheAsync(uint itemId)
    {
        try
        {
            var result = await FetchMobDropsAsync(itemId);
            cache[itemId] = result;
        }
        catch
        {
            cache[itemId] = null;
        }
    }

    private async Task<List<MobDropInfo>?> FetchMobDropsAsync(uint itemId)
    {
        var url = string.Format(GarlandItemUrl, itemId);
        var response = await httpClient.GetAsync(url);

        if (!response.IsSuccessStatusCode)
            return null;

        // Limit response size (64KB)
        var content = response.Content;
        var contentLength = content.Headers.ContentLength;
        if (contentLength > 65536)
            return null;

        var json = await content.ReadAsStringAsync();
        if (json.Length > 65536)
            return null;

        return ParseMobDrops(json);
    }

    private List<MobDropInfo>? ParseMobDrops(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Check if item has a 'drops' array
        if (!root.TryGetProperty("item", out var item))
            return null;
        if (!item.TryGetProperty("drops", out var drops))
            return null;
        if (drops.ValueKind != JsonValueKind.Array || drops.GetArrayLength() == 0)
            return null;

        // Collect mob IDs from the drops array
        var mobIds = new HashSet<string>();
        foreach (var drop in drops.EnumerateArray())
        {
            var mobId = drop.ValueKind == JsonValueKind.Number
                ? drop.GetInt64().ToString()
                : drop.GetString();
            if (mobId != null) mobIds.Add(mobId);
        }

        if (mobIds.Count == 0) return null;

        // Find mob details in partials array
        var mobs = new List<MobDropInfo>();
        if (!root.TryGetProperty("partials", out var partials))
            return null;

        foreach (var partial in partials.EnumerateArray())
        {
            if (!partial.TryGetProperty("type", out var type) || type.GetString() != "mob")
                continue;
            if (!partial.TryGetProperty("id", out var id))
                continue;

            var idStr = id.GetString() ?? id.ToString();
            if (idStr == null || !mobIds.Contains(idStr)) continue;

            // Parse the mob ID for later coordinate lookup
            long mobIdLong = 0;
            if (id.ValueKind == JsonValueKind.Number)
                mobIdLong = id.GetInt64();
            else if (id.ValueKind == JsonValueKind.String)
                long.TryParse(id.GetString(), out mobIdLong);

            if (!partial.TryGetProperty("obj", out var obj))
                continue;

            var name = obj.TryGetProperty("n", out var n) ? n.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;

            var level = "";
            if (obj.TryGetProperty("l", out var l))
            {
                level = l.ValueKind == JsonValueKind.String
                    ? l.GetString() ?? ""
                    : l.ToString();
            }

            var zoneName = "";
            uint territoryTypeId = 0;
            uint mapId = 0;
            if (obj.TryGetProperty("z", out var z))
            {
                var zoneId = z.ValueKind == JsonValueKind.Number ? z.GetInt32() : 0;
                if (zoneId > 0)
                {
                    zoneNameCache.TryGetValue(zoneId, out zoneName);
                    if (zoneToTerritoryMap.TryGetValue(zoneId, out var terrInfo))
                    {
                        territoryTypeId = terrInfo.TerritoryTypeId;
                        mapId = terrInfo.MapId;
                    }
                }
                zoneName ??= "";
            }

            mobs.Add(new MobDropInfo
            {
                MobName = name,
                Level = level,
                ZoneName = zoneName,
                MobId = mobIdLong,
                TerritoryTypeId = territoryTypeId,
                MapId = mapId,
            });
        }

        if (mobs.Count == 0) return null;

        // Sort by level (numeric parse, ascending) and take top N
        mobs.Sort((a, b) =>
        {
            var la = ParseFirstLevel(a.Level);
            var lb = ParseFirstLevel(b.Level);
            return la.CompareTo(lb);
        });

        return mobs.Take(MaxMobsPerItem).ToList();
    }

    private static int ParseFirstLevel(string level)
    {
        if (string.IsNullOrEmpty(level)) return 999;
        // Handle ranges like "2 - 4" → take first number
        var span = level.AsSpan();
        var end = 0;
        while (end < span.Length && char.IsDigit(span[end])) end++;
        if (end > 0 && int.TryParse(span[..end], out var val))
            return val;
        return 999;
    }

    /// <summary>
    /// Builds a zone name cache from Lumina's PlaceName sheet.
    /// Garland Tools zone IDs map to PlaceName row IDs.
    /// </summary>
    private void BuildZoneNameCache()
    {
        try
        {
            var placeNameSheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>();
            if (placeNameSheet == null) return;

            foreach (var place in placeNameSheet)
            {
                var name = place.Name.ExtractText();
                if (!string.IsNullOrEmpty(name))
                    zoneNameCache[(int)place.RowId] = name;
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "Failed to build zone name cache for mob drops");
        }
    }

    /// <summary>
    /// Builds a reverse mapping from PlaceName RowId (Garland zone ID) to TerritoryType/Map data.
    /// Needed for constructing MapLinkPayload from Garland's zone IDs.
    /// </summary>
    private void BuildZoneToTerritoryMap()
    {
        try
        {
            var territorySheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
            if (territorySheet == null) return;

            foreach (var territory in territorySheet)
            {
                var placeNameId = territory.PlaceName.RowId;
                if (placeNameId == 0) continue;

                // Prefer the first match (main overworld territory for that zone name)
                if (!zoneToTerritoryMap.ContainsKey((int)placeNameId))
                    zoneToTerritoryMap[(int)placeNameId] = (territory.RowId, territory.Map.RowId);
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "Failed to build zone-to-territory map for mob drops");
        }
    }

    /// <summary>
    /// Fetches and populates map coordinates for mob drops from the Garland Tools mob detail endpoint.
    /// Mob coords from Garland are already in map-coordinate format — no conversion needed.
    /// </summary>
    public void EnrichMobCoords(List<MobDropInfo> mobs)
    {
        var toFetch = mobs.Where(m => m.MobId > 0 && !m.HasMapCoords).ToList();
        if (toFetch.Count == 0) return;

        try
        {
            var tasks = toFetch.Select(async mob =>
            {
                var coords = await FetchMobCoordsAsync(mob.MobId);
                return (mob, coords);
            }).ToArray();

            var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

            foreach (var (mob, coords) in results)
            {
                if (coords.HasValue)
                {
                    mob.MapX = coords.Value.MapX;
                    mob.MapY = coords.Value.MapY;
                }
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "Failed to enrich mob coordinates");
        }
    }

    /// <summary>
    /// Fetches map coordinates for a mob from the Garland Tools mob detail endpoint.
    /// Returns (mapX, mapY) or null if unavailable.
    /// </summary>
    private async Task<(float MapX, float MapY)?> FetchMobCoordsAsync(long mobId)
    {
        if (mobId <= 0) return null;

        try
        {
            var url = string.Format(GarlandMobUrl, mobId);
            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            if (json.Length > 65536) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("mob", out var mob)) return null;
            if (!mob.TryGetProperty("coords", out var coords)) return null;
            if (coords.ValueKind != JsonValueKind.Array || coords.GetArrayLength() < 2) return null;

            var mapX = coords[0].GetSingle();
            var mapY = coords[1].GetSingle();
            return (mapX, mapY);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
