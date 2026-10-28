using System;

namespace Hephaestus.Features.Crafting.Simulation;

/// <summary>
/// Everything about one craft that does not change while it runs: the recipe's totals, and what
/// the crafter's stats turn into per action. Immutable; shared freely across threads.
/// </summary>
/// <param name="BaseProgress">Progress from a 100-potency action — see <see cref="ComputeBaseProgress"/>.</param>
/// <param name="BaseQuality">Quality from a 100-potency action — see <see cref="ComputeBaseQuality"/>.</param>
/// <param name="AllowedActions"><see cref="ActionMask"/> bits; level requirements are checked separately.</param>
/// <param name="SplendorousTools">Splendorous/Cosmic tools: Good is worth x1.75 instead of x1.5.</param>
public readonly record struct CraftParameters(
    int MaxCp,
    int MaxDurability,
    int MaxProgress,
    int MaxQuality,
    int BaseProgress,
    int BaseQuality,
    int JobLevel,
    ulong AllowedActions,
    bool SplendorousTools = false)
{
    /// <summary>
    /// Progress gained by a 100-potency action. The divider/modifier come from the recipe's
    /// RecipeLevelTable row; the modifier only applies when the crafter is not above the recipe's
    /// job level. Same formula as Raphael and Artisan — in single precision like both of them
    /// (raphael-data get_game_settings, Artisan Simulator.BaseProgress): double rounds some stats
    /// one point the other way.
    /// </summary>
    public static int ComputeBaseProgress(int craftsmanship, int jobLevel, int recipeJobLevel, int progressDivider, int progressModifier)
    {
        var value = craftsmanship * 10f / progressDivider + 2f;
        if (jobLevel <= recipeJobLevel)
            value = value * progressModifier / 100f;
        return (int)Math.Floor(value);
    }

    /// <summary>Quality gained by a 100-potency action at zero Inner Quiet, Normal condition.</summary>
    public static int ComputeBaseQuality(int control, int jobLevel, int recipeJobLevel, int qualityDivider, int qualityModifier)
    {
        var value = control * 10f / qualityDivider + 35f;
        if (jobLevel <= recipeJobLevel)
            value = value * qualityModifier / 100f;
        return (int)Math.Floor(value);
    }
}
