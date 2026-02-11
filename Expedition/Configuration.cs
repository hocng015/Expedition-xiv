using Dalamud.Configuration;

namespace Expedition;

/// <summary>
/// Plugin configuration persisted to disk.
/// </summary>
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- General ---
    public bool AutoRepairEnabled { get; set; } = true;
    public int RepairThresholdPercent { get; set; } = 30;
    public bool AutoExtractMateriaEnabled { get; set; } = false;

    // --- Gathering ---
    public bool UseCollectableGathering { get; set; } = true;
    public int GatherRetryLimit { get; set; } = 3;
    public int GatherQuantityBuffer { get; set; } = 0;

    // --- Crafting ---
    public bool UseCollectableCrafting { get; set; } = true;
    public string PreferredSolver { get; set; } = string.Empty;
    public bool CraftSubRecipesFirst { get; set; } = true;
    public int CraftQuantityBuffer { get; set; } = 0;

    // --- Workflow ---
    public int PollingIntervalMs { get; set; } = 1000;
    public int IpcTimeoutMs { get; set; } = 300000;
    public bool PauseOnError { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;

    // --- UI ---
    public bool ShowOverlay { get; set; } = true;
    public bool ShowDetailedStatus { get; set; } = false;

    public void Save()
    {
        DalamudApi.PluginInterface.SavePluginConfig(this);
    }
}
