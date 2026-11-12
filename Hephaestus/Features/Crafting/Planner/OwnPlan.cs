using System;
using System.Collections.Generic;
using System.Globalization;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Features.Crafting.Planner;

/// <summary>
/// A <see cref="BaselineRequest"/> answered by OUR planner instead of raphael-cli: what the request
/// means to <see cref="RotationPlanner"/>, the key its answer is cached under, and whether a rotation
/// carries the item to full quality on paper - which decides who makes it (see
/// <see cref="Driving.CraftSession"/>: the live evaluator follows a rotation that does, the expert
/// solver takes the craft when none does).
/// </summary>
public static class OwnPlan
{
    /// <summary>
    /// Part of the cache key. Raise it whenever the planner can answer differently (a new rule, a retuned
    /// ranking): rotations cached by an older planner are then asked for again instead of trusted.
    /// </summary>
    public const int Version = 2;

    /// <summary>The craft as the planner sees it: nothing left to chance, no duty action, a specialist's actions only where the request allows them.</summary>
    public static CraftParameters Parameters(in BaselineRequest request)
    {
        var allowed = ActionMask.Regular;
        if (!request.Manipulation)
            allowed = ActionMask.Without(allowed, CraftAction.Manipulation);
        if (request.TrainedEye)
            allowed = ActionMask.With(allowed, CraftAction.TrainedEye);
        if (request.HeartAndSoul)
            allowed = ActionMask.With(allowed, CraftAction.HeartAndSoul);
        if (request.QuickInnovation)
            allowed = ActionMask.With(allowed, CraftAction.QuickInnovation);

        return new CraftParameters(
            request.MaxCp, request.MaxDurability, request.MaxProgress, request.MaxQuality,
            request.BaseProgress, request.BaseQuality, request.JobLevel, allowed);
    }

    /// <summary>Everything the answer depends on, and nothing else (the recipe level and the raw stats are already in the base values).</summary>
    public static string CacheKey(in BaselineRequest request)
        => string.Create(CultureInfo.InvariantCulture,
            $"own{Version}|{request.MaxProgress}|{request.MaxQuality}|{request.MaxDurability}|{request.MaxCp}|{request.BaseProgress}|{request.BaseQuality}|{request.JobLevel}|{request.InitialQuality}|{(request.Manipulation ? 1 : 0)}|{(request.TrainedEye ? 1 : 0)}|{(request.HeartAndSoul ? 1 : 0)}|{(request.QuickInnovation ? 1 : 0)}");

    /// <param name="threads">Threads the search may take, at below-normal priority; 0 = all of them (tests).</param>
    public static RotationPlan Solve(in BaselineRequest request, int threads)
        => RotationPlanner.Plan(Parameters(request), request.InitialQuality, request.MaxQuality, threads: threads);

    /// <summary>
    /// Share of the quality bar, in percent, from which a rotation counts as carrying an item that is judged by its
    /// HQ chance: 98% of the bar is a 96% item on paper, and the live evaluator following such a rotation makes more
    /// high-quality items, in fewer steps, than the expert solver starting from nothing (measured 2026-11-12: the
    /// Ingot at 99.4% of the bar on paper - 99.9 HQ in 24.7 steps against 99.4 in 34.0).
    /// </summary>
    public const int NearlyPercent = 98;

    /// <summary>
    /// Who makes the item: true = this rotation does, followed by the live evaluator. It reaches the target on paper -
    /// or, for an item judged by its HQ chance, comes within <see cref="NearlyPercent"/> of the full bar.
    /// </summary>
    public static bool Carries(in CraftParameters parameters, IReadOnlyList<CraftAction> rotation, int initialQuality, int target, bool byHqChance)
        => Reaches(parameters, rotation, initialQuality, byHqChance ? Math.Min(target, (int)((long)parameters.MaxQuality * NearlyPercent / 100)) : target);

    /// <summary>Edits per run of the deep polish: four times the first answer's (measured 2026-11-12: it closes the one step our first answer was behind Raphael's on two of the user's recipes).</summary>
    public const int DeepIterations = 4_000_000;

    /// <summary>
    /// The second, slower look: the polisher again, from the rotation already found, on four times the edits. Returns
    /// that rotation unless it found a better one - it is never worse.
    /// </summary>
    public static RotationPlan Deepen(in BaselineRequest request, RotationPlan first, int threads)
    {
        if (!first.Completed)
            return first;

        var deep = PlanPolisher.Polish(Parameters(request), request.InitialQuality, request.MaxQuality, first.Actions, DeepIterations, threads: threads);
        return RotationPlanner.Better(deep, first) ? deep : first;
    }

    /// <summary>The rotation, played under Normal conditions from <paramref name="initialQuality"/>, finishes the item at <paramref name="target"/> or better.</summary>
    public static bool Reaches(in CraftParameters parameters, IReadOnlyList<CraftAction> rotation, int initialQuality, int target)
    {
        if (rotation.Count == 0)
            return false;

        var played = RotationPlayer.PlayNormal(parameters, rotation);
        return played.Completed && played.Quality + initialQuality >= target;
    }
}
