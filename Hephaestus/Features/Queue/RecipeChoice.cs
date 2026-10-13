using System.Collections.Generic;

namespace Hephaestus.Features.Queue;

/// <summary>One way of making an item: a recipe and the job it belongs to.</summary>
public readonly record struct RecipeOption(uint RecipeId, uint JobId);

/// <summary>
/// An item can have a recipe under several crafting jobs. When something asks for "this ITEM"
/// (a right-click, another plugin) one of them has to be chosen without a prompt:
///
///   1. the job the request came from, when it names one — in the crafting log that is the job tab
///      being browsed, which is the recipe the player is actually looking at;
///   2. else the job the character is on right now, which costs no gearset change;
///   3. else the first recipe, in the game's own order.
/// </summary>
public static class RecipeChoice
{
    /// <param name="contextJob">The job implied by where the request came from; 0 = none.</param>
    /// <param name="currentJob">The character's current job; 0 = unknown.</param>
    public static RecipeOption? Pick(IReadOnlyList<RecipeOption> options, uint contextJob, uint currentJob)
    {
        if (options.Count == 0)
            return null;

        foreach (var wanted in new[] { contextJob, currentJob })
        {
            if (wanted == 0)
                continue;

            foreach (var option in options)
            {
                if (option.JobId == wanted)
                    return option;
            }
        }

        return options[0];
    }
}
