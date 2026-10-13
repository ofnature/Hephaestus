using System;
using System.Collections.Generic;
using System.Linq;

namespace Hephaestus.Features.Workshop;

/// <summary>One material line queued for a phase: the item, how many, and the recipe that makes it (null = not craftable).</summary>
public sealed record WorkshopQueueLine(uint ItemId, string Name, uint Count, uint? RecipeId);

/// <summary>A phase's material lines, kept separate so the queue reads in build order (phase 1, then 2, then 3).</summary>
public sealed record WorkshopQueuePhase(int Number, IReadOnlyList<WorkshopQueueLine> Lines);

/// <summary>
/// Turns a part's build into the craft-queue entries it needs, grouped by phase and ordered phase 1 → 2 → 3.
/// All phases are queued at once; the grouping is what keeps the build in order.
/// </summary>
public static class WorkshopQueue
{
    /// <summary>Resolve each material to the recipe that makes it (null when it cannot be crafted).</summary>
    public static IReadOnlyList<WorkshopQueuePhase> Plan(WorkshopBuild build, Func<uint, uint?> recipeFor)
        => build.Phases
            .Select(p => new WorkshopQueuePhase(p.Number,
                p.Materials.Select(m => new WorkshopQueueLine(m.ItemId, m.Name, m.Count, recipeFor(m.ItemId))).ToList()))
            .ToList();

    /// <summary>The craftable lines as (recipe, count) in phase order, ready to add to the queue. Non-craftable lines are skipped.</summary>
    public static IEnumerable<(uint RecipeId, int Count)> Flatten(IReadOnlyList<WorkshopQueuePhase> phases)
        => phases
            .SelectMany(p => p.Lines)
            .Where(l => l.RecipeId is not null)
            .Select(l => (l.RecipeId!.Value, (int)l.Count));
}
