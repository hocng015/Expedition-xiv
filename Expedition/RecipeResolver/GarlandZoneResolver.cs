namespace Expedition.RecipeResolver;

/// <summary>
/// Resolves Garland Tools zone IDs (which are PlaceName RowIds) to
/// zone names and TerritoryType/Map data. Shared by services that
/// use the Garland Tools API (MobDropLookupService, VendorLookupService).
/// </summary>
public sealed class GarlandZoneResolver
{
    // PlaceName RowId -> zone display name
    private readonly Dictionary<int, string> zoneNames = new();

    // PlaceName RowId -> (TerritoryTypeId, MapId)
    private readonly Dictionary<int, (uint TerritoryTypeId, uint MapId)> zoneToTerritory = new();

    public GarlandZoneResolver()
    {
        BuildZoneNames();
        BuildZoneToTerritoryMap();
    }

    /// <summary>Returns the zone name for a Garland zone ID (PlaceName RowId), or empty string.</summary>
    public string GetZoneName(int garlandZoneId)
        => zoneNames.GetValueOrDefault(garlandZoneId, string.Empty);

    /// <summary>Returns (TerritoryTypeId, MapId) for a Garland zone ID (PlaceName RowId), or null.</summary>
    public (uint TerritoryTypeId, uint MapId)? GetTerritoryInfo(int garlandZoneId)
        => zoneToTerritory.TryGetValue(garlandZoneId, out var info) ? info : null;

    /// <summary>
    /// Builds a zone name cache from Lumina's PlaceName sheet.
    /// Garland Tools zone IDs map to PlaceName row IDs.
    /// </summary>
    private void BuildZoneNames()
    {
        try
        {
            var placeNameSheet = DalamudApi.DataManager.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>();
            if (placeNameSheet == null) return;

            foreach (var place in placeNameSheet)
            {
                var name = place.Name.ExtractText();
                if (!string.IsNullOrEmpty(name))
                    zoneNames[(int)place.RowId] = name;
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "Failed to build zone name cache");
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
                if (!zoneToTerritory.ContainsKey((int)placeNameId))
                    zoneToTerritory[(int)placeNameId] = (territory.RowId, territory.Map.RowId);
            }
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning(ex, "Failed to build zone-to-territory map");
        }
    }
}
