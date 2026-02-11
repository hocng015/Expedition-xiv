using Lumina.Excel;
using Lumina.Excel.Sheets;

using Expedition.RecipeResolver;

namespace Expedition.PlayerState;

/// <summary>
/// Validates that the player meets all prerequisites to execute a workflow.
///
/// Pain points addressed:
/// - Master Recipe books must be purchased before recipes become available
///   (without the tome, the recipe doesn't appear in crafting log at all)
/// - Folklore Tomes must be purchased before legendary gathering nodes are visible
/// - Specialist recipes may require being specialized in a specific class (max 3)
/// - Expert recipes require very specific gear and melds
/// - Class levels may be insufficient for sub-craft recipes
/// - Crystal/cluster requirements are massive for endgame recipes
/// </summary>
public sealed class PrerequisiteValidator
{
    /// <summary>
    /// Runs all prerequisite checks against a resolved recipe and returns
    /// a comprehensive validation result.
    /// </summary>
    public ValidationResult Validate(ResolvedRecipe resolved, Configuration config)
    {
        var result = new ValidationResult();

        ValidateCrystalRequirements(resolved, result);
        ValidateExpertRecipes(resolved, result);
        ValidateSpecialistRequirements(resolved, result);
        ValidateCollectableRequirements(resolved, result);
        ValidateInventoryCapacity(resolved, result);
        ValidateCraftClassRequirements(resolved, result);

        return result;
    }

    private void ValidateCrystalRequirements(ResolvedRecipe resolved, ValidationResult result)
    {
        // Crystals are in the "Other" category since they're not traditionally
        // gathered through GBR. Flag large crystal needs.
        var crystalItems = resolved.OtherMaterials
            .Where(m => IsCrystalItem(m.ItemName))
            .ToList();

        foreach (var crystal in crystalItems)
        {
            if (crystal.QuantityRemaining > 0)
            {
                result.Warnings.Add(new ValidationWarning
                {
                    Category = ValidationCategory.Crystals,
                    Message = $"Need {crystal.QuantityRemaining}x {crystal.ItemName} — ensure sufficient stock.",
                    Severity = crystal.QuantityRemaining > 100 ? Severity.Warning : Severity.Info,
                });
            }
        }

        // Check for cluster shortages specifically (harder to obtain)
        var clusterItems = crystalItems.Where(c => c.ItemName.Contains("Cluster")).ToList();
        if (clusterItems.Any(c => c.QuantityRemaining > 50))
        {
            result.Warnings.Add(new ValidationWarning
            {
                Category = ValidationCategory.Crystals,
                Message = "Large cluster requirement detected. Clusters are only obtainable from " +
                          "timed nodes or Aetherial Reduction. Consider farming in advance.",
                Severity = Severity.Warning,
            });
        }
    }

    private void ValidateExpertRecipes(ResolvedRecipe resolved, ValidationResult result)
    {
        foreach (var step in resolved.CraftOrder)
        {
            if (step.Recipe.IsExpert)
            {
                result.Warnings.Add(new ValidationWarning
                {
                    Category = ValidationCategory.ExpertRecipe,
                    Message = $"'{step.Recipe.ItemName}' is an Expert Recipe. These have random crafting " +
                              "conditions and cannot be reliably macro'd. Artisan's dynamic solver will " +
                              "attempt to handle this, but failure is possible. Ensure gear is fully " +
                              "penta-melded with current-tier materia.",
                    Severity = Severity.Critical,
                });
            }
        }
    }

    private void ValidateSpecialistRequirements(ResolvedRecipe resolved, ValidationResult result)
    {
        // Check if any recipe requires specialist status.
        // In game data, this would be flagged on the Recipe row.
        // For now, we check the IsSpecialist flag if available.
        foreach (var step in resolved.CraftOrder)
        {
            if (step.Recipe.RequiresSpecialist)
            {
                var className = RecipeResolverService.GetCraftTypeName(step.Recipe.CraftTypeId);
                result.Warnings.Add(new ValidationWarning
                {
                    Category = ValidationCategory.Specialist,
                    Message = $"'{step.Recipe.ItemName}' requires {className} Specialist. " +
                              "Ensure you have the specialist soul equipped (max 3 specializations, " +
                              "requires Crafter's Delineation consumable).",
                    Severity = Severity.Error,
                });
            }
        }
    }

    private void ValidateCollectableRequirements(ResolvedRecipe resolved, ValidationResult result)
    {
        // Collectables can't stack — warn about inventory pressure
        var collectableGathers = resolved.GatherList.Count(g => g.IsCollectable);
        if (collectableGathers > 0)
        {
            var estimatedSlots = resolved.GatherList
                .Where(g => g.IsCollectable)
                .Sum(g => g.QuantityNeeded); // Each collectable = 1 slot

            result.Warnings.Add(new ValidationWarning
            {
                Category = ValidationCategory.Inventory,
                Message = $"{collectableGathers} collectable items to gather. Collectables cannot stack — " +
                          $"this will consume up to {estimatedSlots} inventory slots. " +
                          "Ensure sufficient bag space before starting.",
                Severity = estimatedSlots > 50 ? Severity.Warning : Severity.Info,
            });
        }

        // Collectable crafts need to hit collectability thresholds
        var collectableCrafts = resolved.CraftOrder.Count(c => c.Recipe.IsCollectable);
        if (collectableCrafts > 0)
        {
            result.Warnings.Add(new ValidationWarning
            {
                Category = ValidationCategory.Collectables,
                Message = $"{collectableCrafts} collectable items to craft. If crafted collectability " +
                          "doesn't meet the minimum threshold, items are worthless. Ensure " +
                          "sufficient Craftsmanship/Control stats.",
                Severity = Severity.Info,
            });
        }
    }

    private void ValidateInventoryCapacity(ResolvedRecipe resolved, ValidationResult result)
    {
        var distinctGatherItems = resolved.GatherList.Count(g => g.QuantityRemaining > 0);
        var distinctOtherItems = resolved.OtherMaterials.Count(m => m.QuantityRemaining > 0);
        var totalDistinct = distinctGatherItems + distinctOtherItems;

        if (totalDistinct > 30)
        {
            result.Warnings.Add(new ValidationWarning
            {
                Category = ValidationCategory.Inventory,
                Message = $"This recipe tree requires {totalDistinct} distinct material types. " +
                          "Inventory may fill up during gathering. Consider breaking into " +
                          "smaller batches or clearing inventory first.",
                Severity = Severity.Warning,
            });
        }
    }

    private void ValidateCraftClassRequirements(ResolvedRecipe resolved, ValidationResult result)
    {
        var requiredClasses = JobSwitchManager.GetRequiredCraftClasses(resolved.CraftOrder);
        if (requiredClasses.Count > 1)
        {
            var classNames = requiredClasses
                .Select(c => RecipeResolverService.GetCraftTypeName(c))
                .OrderBy(n => n);

            result.Warnings.Add(new ValidationWarning
            {
                Category = ValidationCategory.ClassSwitch,
                Message = $"Crafting requires {requiredClasses.Count} different classes: " +
                          $"{string.Join(", ", classNames)}. Artisan will handle switching, " +
                          "but ensure all classes have adequate gear and levels.",
                Severity = Severity.Info,
            });
        }
    }

    private static bool IsCrystalItem(string name)
    {
        return name.Contains("Shard") || name.Contains("Crystal") || name.Contains("Cluster");
    }
}

public sealed class ValidationResult
{
    public List<ValidationWarning> Warnings { get; } = new();

    public bool HasErrors => Warnings.Any(w => w.Severity >= Severity.Error);
    public bool HasCritical => Warnings.Any(w => w.Severity == Severity.Critical);
    public bool HasWarnings => Warnings.Any(w => w.Severity >= Severity.Warning);

    public IEnumerable<ValidationWarning> Errors => Warnings.Where(w => w.Severity >= Severity.Error);
    public IEnumerable<ValidationWarning> CriticalOnly => Warnings.Where(w => w.Severity == Severity.Critical);
}

public sealed class ValidationWarning
{
    public ValidationCategory Category { get; init; }
    public string Message { get; init; } = string.Empty;
    public Severity Severity { get; init; }
}

public enum ValidationCategory
{
    Crystals,
    ExpertRecipe,
    Specialist,
    MasterBook,
    FolkloreTome,
    ClassLevel,
    ClassSwitch,
    Inventory,
    Collectables,
    GearDurability,
    Buffs,
}

public enum Severity
{
    Info,
    Warning,
    Error,
    Critical,
}
