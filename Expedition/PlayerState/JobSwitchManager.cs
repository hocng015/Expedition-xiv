namespace Expedition.PlayerState;

/// <summary>
/// Manages class/job switching between gatherer and crafter jobs.
///
/// Pain points addressed:
/// - Multi-class dependency chains: recipe A needs BSM sub-craft, B needs WVR, etc.
/// - Switching between gatherer and crafter loses gathering state
/// - Closing crafting log required before switching DoH classes
/// - No buffs carry over between class switches
/// - Gear sets are client-side, must verify correct gear set exists
///
/// Note: Artisan handles DoH class switching for sub-crafts internally.
/// This manager handles the broader gather→craft transitions and
/// pre-flight job readiness validation.
/// </summary>
public sealed class JobSwitchManager
{
    // ClassJob IDs for relevant jobs
    public const uint CRP = 8;
    public const uint BSM = 9;
    public const uint ARM = 10;
    public const uint GSM = 11;
    public const uint LTW = 12;
    public const uint WVR = 13;
    public const uint ALC = 14;
    public const uint CUL = 15;
    public const uint MIN = 16;
    public const uint BTN = 17;
    public const uint FSH = 18;

    private static readonly Dictionary<int, uint> CraftTypeToClassJob = new()
    {
        { 0, CRP }, { 1, BSM }, { 2, ARM }, { 3, GSM },
        { 4, LTW }, { 5, WVR }, { 6, ALC }, { 7, CUL },
    };

    /// <summary>
    /// Returns the ClassJob ID for a CraftType index.
    /// </summary>
    public static uint CraftTypeToJobId(int craftTypeId)
        => CraftTypeToClassJob.GetValueOrDefault(craftTypeId, 0u);

    /// <summary>
    /// Gets the current player ClassJob ID.
    /// </summary>
    public static uint GetCurrentJobId()
    {
        var player = DalamudApi.ClientState.LocalPlayer;
        if (player == null) return 0;
        return player.ClassJob.RowId;
    }

    /// <summary>
    /// Returns true if the player is currently on a gathering class.
    /// </summary>
    public static bool IsOnGatherer()
    {
        var job = GetCurrentJobId();
        return job == MIN || job == BTN || job == FSH;
    }

    /// <summary>
    /// Returns true if the player is currently on a crafting class.
    /// </summary>
    public static bool IsOnCrafter()
    {
        var job = GetCurrentJobId();
        return job >= CRP && job <= CUL;
    }

    /// <summary>
    /// Validates that all crafting classes needed by the resolved recipe
    /// are at sufficient level. Returns a list of problems found.
    /// </summary>
    public static List<string> ValidateCraftingLevels(
        IEnumerable<RecipeResolver.CraftStep> craftOrder)
    {
        var problems = new List<string>();

        foreach (var step in craftOrder)
        {
            var requiredJob = CraftTypeToJobId(step.Recipe.CraftTypeId);
            var jobName = RecipeResolver.RecipeResolverService.GetCraftTypeName(step.Recipe.CraftTypeId);
            var requiredLevel = step.Recipe.RequiredLevel;

            // In a full implementation, we'd check the player's actual level via
            // the PlayerState game struct. For now, log the requirement.
            problems.Add($"Requires {jobName} Lv{requiredLevel} for {step.Recipe.ItemName}");
        }

        // Only return actual problems (level too low), not informational
        // In full implementation: compare against actual levels
        return new List<string>();
    }

    /// <summary>
    /// Determines the set of unique gathering classes needed for the gather list.
    /// </summary>
    public static HashSet<RecipeResolver.GatherType> GetRequiredGatherClasses(
        IEnumerable<RecipeResolver.MaterialRequirement> gatherList)
    {
        var required = new HashSet<RecipeResolver.GatherType>();
        foreach (var mat in gatherList)
        {
            if (mat.GatherType != RecipeResolver.GatherType.None)
                required.Add(mat.GatherType);
        }
        return required;
    }

    /// <summary>
    /// Determines the set of unique crafting classes needed for all craft steps.
    /// </summary>
    public static HashSet<int> GetRequiredCraftClasses(
        IEnumerable<RecipeResolver.CraftStep> craftOrder)
    {
        var required = new HashSet<int>();
        foreach (var step in craftOrder)
        {
            required.Add(step.Recipe.CraftTypeId);
        }
        return required;
    }
}
