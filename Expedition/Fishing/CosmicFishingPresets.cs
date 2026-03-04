using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Expedition.Fishing;

/// <summary>
/// JSON-backed store for Cosmic Exploration fishing preset overrides.
/// Stored as 'cosmic_fishing_presets.json' alongside the plugin config.
///
/// When Expedition detects ICE entering a fishing mission, it checks this store
/// for preset overrides. If found, Expedition injects the overrides into AutoHook
/// after ICE loads its defaults — replacing ICE's generic presets with mission-optimized ones.
///
/// Data hierarchy:
///   1. Per-mission overrides (mission ID → AH4_ strings) — highest priority
///   2. Per-type defaults (mission type → AH4_ strings) — fallback
///   3. No override → ICE's built-in presets are used (no interference)
/// </summary>
public sealed class CosmicFishingPresets
{
    private const string FileName = "cosmic_fishing_presets.json";

    /// <summary>
    /// Per-mission preset overrides. Key = mission ID, Value = list of AH4_ compressed preset strings.
    /// When multiple presets are provided, they're all loaded into AutoHook (for multi-bait configs).
    /// </summary>
    public Dictionary<uint, List<string>> Overrides { get; set; } = new();

    /// <summary>
    /// Fallback presets by mission scoring type. Used when no per-mission override exists.
    /// </summary>
    public Dictionary<CosmicFishingMissionType, List<string>> TypeDefaults { get; set; } = new();

    /// <summary>
    /// Total number of configured overrides (mission-specific + type defaults).
    /// </summary>
    [JsonIgnore]
    public int TotalOverrideCount => Overrides.Count + TypeDefaults.Count;

    /// <summary>
    /// Returns true if there's a preset override for the given mission ID.
    /// </summary>
    public bool HasOverride(uint missionId)
    {
        return Overrides.ContainsKey(missionId) && Overrides[missionId].Count > 0;
    }

    /// <summary>
    /// Returns true if there's a type-level default for the given mission type.
    /// </summary>
    public bool HasTypeDefault(CosmicFishingMissionType type)
    {
        return TypeDefaults.ContainsKey(type) && TypeDefaults[type].Count > 0;
    }

    /// <summary>
    /// Gets the presets for a mission. Checks mission-specific overrides first,
    /// then falls back to type defaults. Returns null if no overrides exist.
    /// </summary>
    public List<string>? GetPresetsForMission(uint missionId, CosmicFishingMissionType type)
    {
        // 1. Check mission-specific override
        if (Overrides.TryGetValue(missionId, out var missionPresets) && missionPresets.Count > 0)
            return missionPresets;

        // 2. Check type-level default
        if (TypeDefaults.TryGetValue(type, out var typePresets) && typePresets.Count > 0)
            return typePresets;

        // 3. No override — ICE's defaults will be used
        return null;
    }

    /// <summary>
    /// Imports a preset for a specific mission ID. Appends to existing presets for that mission.
    /// </summary>
    public void ImportPreset(uint missionId, string ah4String)
    {
        if (!Overrides.TryGetValue(missionId, out var list))
        {
            list = new List<string>();
            Overrides[missionId] = list;
        }
        list.Add(ah4String);
    }

    /// <summary>
    /// Replaces all presets for a specific mission ID.
    /// </summary>
    public void SetPresets(uint missionId, List<string> presets)
    {
        Overrides[missionId] = new List<string>(presets);
    }

    /// <summary>
    /// Imports a type-level default preset. Appends to existing presets for that type.
    /// </summary>
    public void ImportTypeDefault(CosmicFishingMissionType type, string ah4String)
    {
        if (!TypeDefaults.TryGetValue(type, out var list))
        {
            list = new List<string>();
            TypeDefaults[type] = list;
        }
        list.Add(ah4String);
    }

    /// <summary>
    /// Clears all presets for a specific mission ID.
    /// </summary>
    public void ClearOverride(uint missionId)
    {
        Overrides.Remove(missionId);
    }

    /// <summary>
    /// Clears all presets for a specific mission type.
    /// </summary>
    public void ClearTypeDefault(CosmicFishingMissionType type)
    {
        TypeDefaults.Remove(type);
    }

    /// <summary>
    /// Clears all overrides and type defaults.
    /// </summary>
    public void ClearAll()
    {
        Overrides.Clear();
        TypeDefaults.Clear();
    }

    // ─── Persistence ──────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Loads presets from disk. Returns a new empty instance if the file doesn't exist.
    /// </summary>
    public static CosmicFishingPresets Load()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
            {
                DalamudApi.Log.Information("[CosmicFishing] No preset file found, starting with empty presets.");
                return new CosmicFishingPresets();
            }

            var json = File.ReadAllText(path);
            var result = JsonSerializer.Deserialize<CosmicFishingPresets>(json, JsonOptions);
            if (result == null)
            {
                DalamudApi.Log.Warning("[CosmicFishing] Failed to deserialize preset file, starting empty.");
                return new CosmicFishingPresets();
            }

            DalamudApi.Log.Information(
                $"[CosmicFishing] Loaded {result.Overrides.Count} mission overrides, " +
                $"{result.TypeDefaults.Count} type defaults.");
            return result;
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[CosmicFishing] Error loading presets: {ex.Message}");
            return new CosmicFishingPresets();
        }
    }

    /// <summary>
    /// Saves presets to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var path = GetFilePath();
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(path, json);
            DalamudApi.Log.Debug("[CosmicFishing] Presets saved.");
        }
        catch (Exception ex)
        {
            DalamudApi.Log.Warning($"[CosmicFishing] Error saving presets: {ex.Message}");
        }
    }

    private static string GetFilePath()
    {
        var configDir = DalamudApi.PluginInterface.GetPluginConfigDirectory();
        return Path.Combine(configDir, FileName);
    }
}
