using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Lumina.Excel.Sheets;

namespace Expedition.Fishing;

public record FishingSpotResult(Vector3 Position, float Distance, uint BaseId, string Name);

/// <summary>
/// Scans IObjectTable for nearest fishing spot EventObj.
/// Caches the EObj->FishingSpot mapping from Lumina on first use.
/// </summary>
public static class FishingSpotFinder
{
    private static HashSet<uint>? _fishingSpotBaseIds;

    public static FishingSpotResult? FindNearest(float maxRange = 150f)
    {
        var objectTable = DalamudApi.ObjectTable;
        var player = objectTable.LocalPlayer;
        if (player == null) return null;

        EnsureCache();
        if (_fishingSpotBaseIds == null || _fishingSpotBaseIds.Count == 0) return null;

        var playerPos = player.Position;
        FishingSpotResult? closest = null;

        foreach (var obj in objectTable)
        {
            if (obj.ObjectKind != ObjectKind.EventObj) continue;
            if (!_fishingSpotBaseIds.Contains(obj.BaseId)) continue;

            var dist = Vector3.Distance(playerPos, obj.Position);
            if (dist > maxRange) continue;

            if (closest == null || dist < closest.Distance)
            {
                closest = new FishingSpotResult(
                    obj.Position,
                    dist,
                    obj.BaseId,
                    obj.Name.TextValue);
            }
        }

        return closest;
    }

    private static void EnsureCache()
    {
        if (_fishingSpotBaseIds != null) return;

        _fishingSpotBaseIds = new HashSet<uint>();
        var dataManager = DalamudApi.DataManager;

        var eobjSheet = dataManager.GetExcelSheet<EObj>();
        var fishingSpotSheet = dataManager.GetExcelSheet<FishingSpot>();
        if (eobjSheet == null || fishingSpotSheet == null) return;

        // Build set of valid FishingSpot RowIds
        var validFishingSpotIds = new HashSet<uint>();
        foreach (var spot in fishingSpotSheet)
        {
            if (spot.RowId != 0)
                validFishingSpotIds.Add(spot.RowId);
        }

        // EObj.Data references FishingSpot rows
        foreach (var eobj in eobjSheet)
        {
            if (validFishingSpotIds.Contains(eobj.Data.RowId))
                _fishingSpotBaseIds.Add(eobj.RowId);
        }

        DalamudApi.Log.Information(
            $"[Fishing] Cached {_fishingSpotBaseIds.Count} fishing spot EObj IDs " +
            $"from {validFishingSpotIds.Count} fishing spots.");
    }

    public static void InvalidateCache()
    {
        _fishingSpotBaseIds = null;
    }
}
