using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Expedition.RecipeResolver;

/// <summary>
/// Builds a reverse lookup from Item ID to vendor information at startup.
/// Covers both GilShop (standard gil vendors) and SpecialShop (tomestones, scrips, etc.).
/// Falls back to Garland Tools NPC endpoint for vendors missing Level sheet location data.
/// </summary>
public sealed class VendorLookupService : IDisposable
{
    private const string GarlandNpcUrl = "https://www.garlandtools.org/db/doc/npc/en/2/{0}.json";
    private const int RequestTimeoutMs = 5000;

    private readonly Dictionary<uint, VendorInfo> vendorsByItemId = new();

    // NPC location data: ENpcBase RowId -> (TerritoryTypeId, ZoneName, MapId, MapX, MapY)
    private readonly Dictionary<uint, (uint TerritoryTypeId, string ZoneName, uint MapId, float MapX, float MapY)> npcLocations = new();

    // NPC names: ENpcBase RowId -> display name
    private readonly Dictionary<uint, string> npcNames = new();

    private readonly HttpClient httpClient;
    private readonly GarlandZoneResolver zoneResolver;

    public VendorLookupService(GarlandZoneResolver zoneResolver)
    {
        this.zoneResolver = zoneResolver;
        httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs),
        };
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Expedition-FFXIV-Plugin");

        var sw = Stopwatch.StartNew();

        try
        {
            BuildCache();
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Error(ex, "Failed to build vendor cache");
        }

        sw.Stop();
        DalamudApi.Log.Information($"Vendor cache built: {vendorsByItemId.Count} vendored items in {sw.ElapsedMilliseconds}ms.");
    }

    /// <summary>Returns vendor info for the given item, or null if no vendor sells it.</summary>
    public VendorInfo? GetVendorInfo(uint itemId)
        => vendorsByItemId.GetValueOrDefault(itemId);

    /// <summary>Returns true if the item is sold by at least one vendor.</summary>
    public bool IsVendorItem(uint itemId)
        => vendorsByItemId.ContainsKey(itemId);

    private void BuildCache()
    {
        var itemSheet = DalamudApi.DataManager.GetExcelSheet<Item>()!;
        var npcBaseSheet = DalamudApi.DataManager.GetExcelSheet<ENpcBase>()!;
        var npcResidentSheet = DalamudApi.DataManager.GetExcelSheet<ENpcResident>()!;
        var gilShopSheet = DalamudApi.DataManager.GetExcelSheet<GilShop>()!;
        var territorySheet = DalamudApi.DataManager.GetExcelSheet<TerritoryType>()!;
        var levelSheet = DalamudApi.DataManager.GetExcelSheet<Level>()!;
        var mapSheet = DalamudApi.DataManager.GetExcelSheet<Map>()!;

        // Step 1: Build NPC name lookup
        foreach (var npc in npcResidentSheet)
        {
            var name = npc.Singular.ExtractText();
            if (!string.IsNullOrEmpty(name))
                npcNames[npc.RowId] = name;
        }

        // Step 2: Build NPC location lookup from Level sheet
        // Level rows with Type=8 are NPC spawn positions
        // Extract world coords (X, Z) and Map RowId, then convert to map-coordinate space
        foreach (var level in levelSheet)
        {
            if (level.Type != 8) continue;
            var objectId = level.Object.RowId;
            if (objectId == 0) continue;
            if (npcLocations.ContainsKey(objectId)) continue;

            var territoryId = level.Territory.RowId;
            if (territoryId == 0) continue;

            var territory = territorySheet.GetRowOrDefault(territoryId);
            if (territory == null) continue;

            var zoneName = territory.Value.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
            if (string.IsNullOrEmpty(zoneName)) continue;

            // Extract map coordinates from world position
            var mapId = level.Map.RowId;
            float mapX = 0, mapY = 0;

            if (mapId != 0)
            {
                var mapRow = mapSheet.GetRowOrDefault(mapId);
                if (mapRow != null)
                {
                    var sizeFactor = mapRow.Value.SizeFactor;
                    var offsetX = mapRow.Value.OffsetX;
                    var offsetY = mapRow.Value.OffsetY;
                    // Level.X → map X, Level.Z → map Y (Level.Y is altitude)
                    mapX = ConvertWorldToMap(level.X, sizeFactor, offsetX);
                    mapY = ConvertWorldToMap(level.Z, sizeFactor, offsetY);
                }
            }

            npcLocations[objectId] = (territoryId, zoneName, mapId, mapX, mapY);
        }

        // Step 3: Iterate ENpcBase to find shop handlers
        // ENpcData values are multi-target RowId links where the upper 16 bits encode
        // the handler type (which sheet) and the full value IS the row ID for that sheet.
        //   GilShop     handler type = 0x0004  (row IDs start at 0x00040000 = 262144)
        //   SpecialShop handler type = 0x001B  (row IDs start at 0x001B0000 = 1769472)
        // NOTE: The full data value is used as the shop row ID, NOT (data & 0xFFFF).
        var gilShopNpcs = new Dictionary<uint, List<uint>>(); // GilShop RowId -> list of NPC RowIds
        var specialShopNpcs = new Dictionary<uint, List<uint>>(); // SpecialShop RowId -> list of NPC RowIds

        foreach (var npc in npcBaseSheet)
        {
            if (npc.RowId == 0) continue;

            for (var i = 0; i < npc.ENpcData.Count; i++)
            {
                var data = npc.ENpcData[i].RowId;
                if (data == 0) continue;

                var handlerType = data >> 16;

                switch (handlerType)
                {
                    case 0x0004: // GilShop
                        if (!gilShopNpcs.TryGetValue(data, out var gilList))
                        {
                            gilList = new List<uint>();
                            gilShopNpcs[data] = gilList;
                        }
                        gilList.Add(npc.RowId);
                        break;

                    case 0x001B: // SpecialShop
                        if (!specialShopNpcs.TryGetValue(data, out var specList))
                        {
                            specList = new List<uint>();
                            specialShopNpcs[data] = specList;
                        }
                        specList.Add(npc.RowId);
                        break;
                }
            }
        }

        // Step 4: Process GilShop items
        ProcessGilShops(itemSheet, gilShopSheet, gilShopNpcs);

        // Step 5: Process SpecialShop items
        ProcessSpecialShops(itemSheet, specialShopNpcs);

        // Step 6: Enrich vendors missing map coordinates via Garland Tools NPC endpoint.
        // Some vendor NPCs don't have Type=8 entries in the Level sheet (e.g. instanced NPCs,
        // housing district vendors). The Garland Tools NPC endpoint provides their coordinates.
        EnrichMissingVendorLocations();
    }

    private void ProcessGilShops(
        ExcelSheet<Item> itemSheet,
        ExcelSheet<GilShop> gilShopSheet,
        Dictionary<uint, List<uint>> gilShopNpcs)
    {
        SubrowExcelSheet<GilShopItem>? gilShopItemSheet = null;
        try
        {
            gilShopItemSheet = DalamudApi.DataManager.GetSubrowExcelSheet<GilShopItem>();
        }
        catch
        {
            DalamudApi.Log.Warning("Failed to load GilShopItem subrow sheet. Gil vendor lookup unavailable.");
            return;
        }

        if (gilShopItemSheet == null) return;

        foreach (var (shopId, npcIds) in gilShopNpcs)
        {
            // Get the best NPC for this shop (prefer one with a known location)
            var (bestNpcId, bestNpcName, bestZone, bestTerritoryId, bestMapId, bestMapX, bestMapY) = GetBestNpc(npcIds);
            if (bestNpcName == null) continue;

            // Iterate items in this GilShop
            try
            {
                var subrowCount = gilShopItemSheet.GetSubrowCount(shopId);
                for (ushort sub = 0; sub < subrowCount; sub++)
                {
                    var shopItem = gilShopItemSheet.GetSubrow(shopId, sub);
                    var itemId = shopItem.Item.RowId;
                    if (itemId == 0) continue;

                    // Skip if we already have a gil vendor for this item
                    if (vendorsByItemId.TryGetValue(itemId, out var existing) && existing.IsGilShop) continue;

                    var item = itemSheet.GetRowOrDefault(itemId);
                    if (item == null) continue;

                    var price = item.Value.PriceMid;
                    if (price == 0) continue; // Skip free/unpurchasable items

                    vendorsByItemId[itemId] = new VendorInfo
                    {
                        NpcName = bestNpcName,
                        ZoneName = bestZone,
                        PricePerUnit = price,
                        CurrencyName = "Gil",
                        CurrencyItemId = 1,
                        IsGilShop = true,
                        NpcId = bestNpcId,
                        TerritoryTypeId = bestTerritoryId,
                        MapId = bestMapId,
                        MapX = bestMapX,
                        MapY = bestMapY,
                    };
                }
            }
            catch
            {
                // Some shop IDs may not have valid subrow data
            }
        }
    }

    private void ProcessSpecialShops(
        ExcelSheet<Item> itemSheet,
        Dictionary<uint, List<uint>> specialShopNpcs)
    {
        ExcelSheet<SpecialShop>? specialShopSheet = null;
        try
        {
            specialShopSheet = DalamudApi.DataManager.GetExcelSheet<SpecialShop>();
        }
        catch
        {
            DalamudApi.Log.Warning("Failed to load SpecialShop sheet. Special vendor lookup unavailable.");
            return;
        }

        if (specialShopSheet == null) return;

        foreach (var (shopId, npcIds) in specialShopNpcs)
        {
            var (bestNpcId, bestNpcName, bestZone, bestTerritoryId, bestMapId, bestMapX, bestMapY) = GetBestNpc(npcIds);
            if (bestNpcName == null) continue;

            var shop = specialShopSheet.GetRowOrDefault(shopId);
            if (shop == null) continue;

            foreach (var entry in shop.Value.Item)
            {
                // ReceiveItems: what the player gets
                foreach (var receiveItem in entry.ReceiveItems)
                {
                    var itemId = receiveItem.Item.RowId;
                    if (itemId == 0) continue;

                    // Don't overwrite existing gil vendors (gil is simpler/cheaper)
                    if (vendorsByItemId.TryGetValue(itemId, out var existing) && existing.IsGilShop) continue;

                    // Get cost info from the first valid cost entry
                    uint costAmount = 0;
                    string currencyName = "Unknown";
                    uint currencyItemId = 0;

                    foreach (var cost in entry.ItemCosts)
                    {
                        var costItemId = cost.ItemCost.RowId;
                        if (costItemId == 0) continue;

                        costAmount = cost.CurrencyCost;
                        currencyItemId = costItemId;

                        // Get the currency item name
                        var costItem = itemSheet.GetRowOrDefault(costItemId);
                        if (costItem != null)
                            currencyName = costItem.Value.Name.ExtractText();

                        break; // Use first valid cost
                    }

                    if (costAmount == 0) continue;

                    vendorsByItemId[itemId] = new VendorInfo
                    {
                        NpcName = bestNpcName,
                        ZoneName = bestZone,
                        PricePerUnit = costAmount,
                        CurrencyName = currencyName,
                        CurrencyItemId = currencyItemId,
                        IsGilShop = false,
                        NpcId = bestNpcId,
                        TerritoryTypeId = bestTerritoryId,
                        MapId = bestMapId,
                        MapX = bestMapX,
                        MapY = bestMapY,
                    };
                }
            }
        }
    }

    /// <summary>
    /// From a list of NPC IDs that host a shop, pick the best one
    /// (preferring NPCs with known locations and names).
    /// </summary>
    private (uint NpcId, string? NpcName, string Zone, uint TerritoryId, uint MapId, float MapX, float MapY) GetBestNpc(List<uint> npcIds)
    {
        // Prefer NPCs that have both a name and a location
        foreach (var npcId in npcIds)
        {
            if (npcNames.TryGetValue(npcId, out var name) && npcLocations.TryGetValue(npcId, out var loc))
                return (npcId, name, loc.ZoneName, loc.TerritoryTypeId, loc.MapId, loc.MapX, loc.MapY);
        }

        // Fall back to NPC with just a name
        foreach (var npcId in npcIds)
        {
            if (npcNames.TryGetValue(npcId, out var name))
                return (npcId, name, string.Empty, 0, 0, 0f, 0f);
        }

        return (0, null, string.Empty, 0, 0, 0f, 0f);
    }

    /// <summary>
    /// Converts a world coordinate (from Level sheet) to map coordinate space
    /// (suitable for MapLinkPayload). Uses the standard SE formula.
    /// Level.X → map X, Level.Z → map Y (Level.Y is altitude in world space).
    /// </summary>
    private static float ConvertWorldToMap(float worldCoord, ushort sizeFactor, short offset)
    {
        var scale = sizeFactor / 100.0f;
        return 41.0f / scale * ((worldCoord + offset * scale + 1024.0f) / 2048.0f) + 1.0f;
    }

    /// <summary>
    /// Enriches vendor entries that are missing map coordinates by fetching NPC location data
    /// from the Garland Tools NPC endpoint. Only fetches for NPCs not found in the Level sheet.
    /// </summary>
    private void EnrichMissingVendorLocations()
    {
        // Identify unique NPC IDs that lack coordinates
        var npcIdsToEnrich = vendorsByItemId.Values
            .Where(v => !v.HasMapCoords && v.NpcId > 0)
            .Select(v => v.NpcId)
            .Distinct()
            .ToList();

        if (npcIdsToEnrich.Count == 0) return;

        DalamudApi.Log.Information($"Enriching {npcIdsToEnrich.Count} vendor NPCs with missing coordinates via Garland Tools...");

        // Fetch NPC data concurrently
        var npcLocationResults = new Dictionary<uint, (string ZoneName, uint TerritoryTypeId, uint MapId, float MapX, float MapY)>();

        try
        {
            var tasks = npcIdsToEnrich.Select(async npcId =>
            {
                var result = await FetchNpcLocationAsync(npcId);
                return (npcId, result);
            }).ToArray();

            var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

            foreach (var (npcId, result) in results)
            {
                if (result.HasValue)
                    npcLocationResults[npcId] = result.Value;
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "Failed to enrich vendor NPC locations from Garland Tools");
            return;
        }

        if (npcLocationResults.Count == 0) return;

        // Replace VendorInfo entries for items whose NPC was enriched.
        // Since VendorInfo uses init-only properties, we create new instances.
        var enrichedCount = 0;
        foreach (var (itemId, vendor) in vendorsByItemId.ToList())
        {
            if (vendor.HasMapCoords) continue;
            if (!npcLocationResults.TryGetValue(vendor.NpcId, out var loc)) continue;

            vendorsByItemId[itemId] = new VendorInfo
            {
                NpcName = vendor.NpcName,
                ZoneName = loc.ZoneName,
                PricePerUnit = vendor.PricePerUnit,
                CurrencyName = vendor.CurrencyName,
                CurrencyItemId = vendor.CurrencyItemId,
                IsGilShop = vendor.IsGilShop,
                NpcId = vendor.NpcId,
                TerritoryTypeId = loc.TerritoryTypeId,
                MapId = loc.MapId,
                MapX = loc.MapX,
                MapY = loc.MapY,
            };
            enrichedCount++;
        }

        DalamudApi.Log.Information(
            $"Enriched {enrichedCount} vendor entries with Garland NPC coordinates " +
            $"({npcLocationResults.Count}/{npcIdsToEnrich.Count} NPCs resolved).");
    }

    /// <summary>
    /// Fetches NPC location data from the Garland Tools NPC endpoint.
    /// Returns zone name, territory/map IDs, and map coordinates, or null if unavailable.
    /// </summary>
    private async Task<(string ZoneName, uint TerritoryTypeId, uint MapId, float MapX, float MapY)?> FetchNpcLocationAsync(uint npcId)
    {
        try
        {
            var url = string.Format(GarlandNpcUrl, npcId);
            var response = await httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            if (json.Length > 65536) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("npc", out var npc)) return null;

            // coords is [mapX, mapY], already in map-coordinate space
            if (!npc.TryGetProperty("coords", out var coords)) return null;
            if (coords.ValueKind != JsonValueKind.Array || coords.GetArrayLength() < 2) return null;

            var mapX = coords[0].GetSingle();
            var mapY = coords[1].GetSingle();

            // zoneid is a PlaceName RowId (same as Garland Tools uses for mobs)
            if (!npc.TryGetProperty("zoneid", out var zoneIdElem)) return null;
            var garlandZoneId = zoneIdElem.GetInt32();

            var zoneName = zoneResolver.GetZoneName(garlandZoneId);
            var terrInfo = zoneResolver.GetTerritoryInfo(garlandZoneId);
            if (terrInfo == null) return null;

            return (zoneName, terrInfo.Value.TerritoryTypeId, terrInfo.Value.MapId, mapX, mapY);
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
