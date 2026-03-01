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
    public bool IncludeSaddlebagInScans { get; set; } = true;

    // --- Gathering ---
    public bool UseCollectableGathering { get; set; } = true;
    public int GatherRetryLimit { get; set; } = 3;
    public int GatherQuantityBuffer { get; set; } = 0;
    public int GatherNoDeltaTimeoutSeconds { get; set; } = 30;
    public int GatherAbsoluteTimeoutSeconds { get; set; } = 60;
    public bool OptimizeGatherRoute { get; set; } = true;
    public bool PrioritizeTimedNodes { get; set; } = true;
    public bool GatherNormalWhileWaiting { get; set; } = true;
    public bool AutoApplyGatheringSkills { get; set; } = true;

    // --- GP Management ---
    public bool UseCordials { get; set; } = true;
    public bool PreferHiCordials { get; set; } = true;
    public int MinGpBeforeGathering { get; set; } = 400;

    // --- Crafting ---
    public bool UseCollectableCrafting { get; set; } = true;
    public string PreferredSolver { get; set; } = string.Empty;
    public string CollectablePreferredSolver { get; set; } = "Raphael Recipe Solver";
    public bool CraftSubRecipesFirst { get; set; } = true;
    public int CraftQuantityBuffer { get; set; } = 0;
    public float CraftStepDelaySeconds { get; set; } = 3.0f;
    public bool TeleportBeforeCrafting { get; set; } = false;
    public int TeleportDestination { get; set; } = 0; // 0=FC Estate, 1=Private Estate, 2=Apartment

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

    // --- Dependency Monitoring ---
    public bool MonitorDependencies { get; set; } = true;
    public int DependencyPollIntervalSeconds { get; set; } = 5;
    public int DependencyWaitTimeoutSeconds { get; set; } = 120;

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

    // --- Insights ---
    public int InsightsRefreshIntervalMinutes { get; set; } = 5;
    public string InsightsDefaultDataCenter { get; set; } = "Aether";
    public bool InsightsAutoRefresh { get; set; } = true;

    // --- Diadem ---
    public bool DiademAutoStartSession { get; set; } = true;
    public bool DiademShowXpNotifications { get; set; } = true;
    public bool DiademUseWindmires { get; set; } = true;
    public bool DiademAutoApplySkillPreset { get; set; } = true;
    public bool DiademEnableAetherCannon { get; set; } = true;
    public bool DiademEnableCordials { get; set; } = true;

    // --- Cosmic Exploration ---
    public int CosmicTargetLevel { get; set; } = 100;
    public int CosmicIceMode { get; set; } // 0=Standard, 1=Relic, 2=Level, 10=Agenda
    public bool CosmicStopAfterCurrent { get; set; }
    public bool CosmicStopOnCosmoCredits { get; set; } = true;
    public int CosmicCosmoCreditsCap { get; set; } = 4000;
    public bool CosmicStopOnLunarCredits { get; set; } = true;
    public int CosmicLunarCreditsCap { get; set; } = 4000;
    public bool CosmicOnlyGrabMission { get; set; }
    public bool CosmicTurninRelic { get; set; }
    public bool CosmicFarmAllRelics { get; set; }
    public bool CosmicRelicCraftersFirst { get; set; } = true;
    public bool CosmicStopOnceRelicFinished { get; set; }
    public bool CosmicRelicSwapJob { get; set; }
    public uint CosmicRelicBattleJob { get; set; }
    public bool CosmicRelicStylist { get; set; } = true;
    public bool CosmicStopWhenLevel { get; set; }
    public bool CosmicStopOnceHitCosmicScore { get; set; }
    public int CosmicCosmicScoreCap { get; set; } = 500_000;

    // --- Fishing ---
    public bool FishingUsePatienceII { get; set; } = true;
    public bool FishingUseChum { get; set; } = true;
    public bool FishingUseThaliaksFavor { get; set; } = true;
    public bool FishingUseCordials { get; set; } = true;

    // --- Activation ---
    public string ActivationKey { get; set; } = string.Empty;

    public void Save()
    {
        DalamudApi.PluginInterface.SavePluginConfig(this);
    }
}
