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
    public bool OptimizeGatherRoute { get; set; } = true;
    public bool PrioritizeTimedNodes { get; set; } = true;
    public bool GatherNormalWhileWaiting { get; set; } = true;

    // --- GP Management ---
    public bool UseCordials { get; set; } = true;
    public bool PreferHiCordials { get; set; } = true;
    public int MinGpBeforeGathering { get; set; } = 400;

    // --- Crafting ---
    public bool UseCollectableCrafting { get; set; } = true;
    public string PreferredSolver { get; set; } = string.Empty;
    public bool CraftSubRecipesFirst { get; set; } = true;
    public int CraftQuantityBuffer { get; set; } = 0;

    // --- Buff Tracking ---
    public bool WarnOnMissingFood { get; set; } = true;
    public bool WarnOnFoodExpiring { get; set; } = true;
    public int FoodExpiryWarningSeconds { get; set; } = 120;

    // --- Durability ---
    public bool CheckDurabilityBeforeStart { get; set; } = true;
    public bool MonitorDurabilityDuringRun { get; set; } = true;
    public int DurabilityWarningPercent { get; set; } = 30;

    // --- Prerequisite Validation ---
    public bool ValidatePrerequisites { get; set; } = true;
    public bool BlockOnCriticalWarnings { get; set; } = true;
    public bool BlockOnExpertRecipes { get; set; } = false;

    // --- Workflow ---
    public int PollingIntervalMs { get; set; } = 1000;
    public int IpcTimeoutMs { get; set; } = 300000;
    public bool PauseOnError { get; set; } = true;
    public bool NotifyOnCompletion { get; set; } = true;
    public int MaxRetryPerTask { get; set; } = 3;
    public int RetryDelayMs { get; set; } = 5000;

    // --- UI ---
    public bool ShowOverlay { get; set; } = true;
    public bool ShowDetailedStatus { get; set; } = false;
    public bool ShowEorzeanTime { get; set; } = true;

    // --- Activation ---
    public string ActivationKey { get; set; } = string.Empty;

    public void Save()
    {
        DalamudApi.PluginInterface.SavePluginConfig(this);
    }
}
