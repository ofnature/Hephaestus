using System;
using System.Collections.Generic;

namespace Hephaestus.Features.Crafting.Simulation;

/// <summary>How one played-out synthesis ended.</summary>
/// <param name="Completed">Progress reached the recipe's total.</param>
/// <param name="StepsUsed">Actions actually used (skipped ones not counted).</param>
/// <param name="StepsSkipped">Rotation steps that could not be used when their turn came.</param>
public readonly record struct PlayResult(bool Completed, int Quality, int StepsUsed, int StepsSkipped, CraftState Final);

/// <summary>What a rotation does across many synthesis attempts.</summary>
public readonly record struct PlaySummary(int Runs, double CompletionRate, double MeanQuality, int MinQuality, int MaxQuality);

/// <summary>
/// Plays a FIXED rotation — what an in-game macro or a static solver result does — either under
/// all-Normal conditions or with the dice the game actually rolls. This is the yardstick the live
/// evaluator has to beat: same recipe, same stats, same dice, a plan that cannot react.
///
/// A step that is not usable when its turn comes (Precise Touch without a Good, not enough CP) is
/// skipped, exactly as a macro line that errors is skipped in game.
/// </summary>
public static class RotationPlayer
{
    /// <summary>Every step under Normal condition, every success roll passing — the solver's view of the world.</summary>
    public static PlayResult PlayNormal(in CraftParameters parameters, IReadOnlyList<CraftAction> rotation)
    {
        var state = CraftSimulator.Initial(parameters);
        var used = 0;
        var skipped = 0;

        foreach (var action in rotation)
        {
            if (CraftSimulator.Use(parameters, state, action, Condition.Normal, success: true, out var next) == ActionError.None)
            {
                state = next;
                used++;
            }
            else
            {
                skipped++;
            }
        }

        return new PlayResult(CraftSimulator.IsSuccess(parameters, state), state.Quality, used, skipped, state);
    }

    /// <summary>One attempt with rolled conditions and rolled success chances.</summary>
    /// <param name="conditionsFlag">The recipe's RecipeLevelTable.ConditionsFlag.</param>
    public static PlayResult PlayRolled(
        in CraftParameters parameters, IReadOnlyList<CraftAction> rotation, ushort conditionsFlag, Random random)
    {
        var odds = ConditionOdds.ForRecipe(conditionsFlag, parameters.JobLevel);
        return PlayRolled(parameters, rotation, odds, ConditionOdds.IsExpert(conditionsFlag), random);
    }

    /// <summary>Many attempts, summarised. Deterministic for a given <paramref name="seed"/>.</summary>
    public static PlaySummary Sample(
        in CraftParameters parameters, IReadOnlyList<CraftAction> rotation, ushort conditionsFlag, int runs, int seed)
    {
        var odds = ConditionOdds.ForRecipe(conditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(conditionsFlag);
        var random = new Random(seed);

        var completed = 0;
        long qualitySum = 0;
        var min = int.MaxValue;
        var max = int.MinValue;
        for (var i = 0; i < runs; i++)
        {
            var result = PlayRolled(parameters, rotation, odds, expert, random);
            if (result.Completed)
                completed++;
            qualitySum += result.Quality;
            min = Math.Min(min, result.Quality);
            max = Math.Max(max, result.Quality);
        }

        return runs == 0
            ? new PlaySummary(0, 0, 0, 0, 0)
            : new PlaySummary(runs, (double)completed / runs, (double)qualitySum / runs, min, max);
    }

    private static PlayResult PlayRolled(
        in CraftParameters parameters, IReadOnlyList<CraftAction> rotation, float[] odds, bool expert, Random random)
    {
        var state = CraftSimulator.Initial(parameters);
        var condition = Condition.Normal; // every synthesis opens on Normal
        var used = 0;
        var skipped = 0;

        foreach (var action in rotation)
        {
            var success = random.Next(100) < CraftSimulator.SuccessPercent(action, condition);
            if (CraftSimulator.Use(parameters, state, action, condition, success, out var next) != ActionError.None)
            {
                skipped++;
                continue;
            }

            state = next;
            used++;
            if (CraftSimulator.SpendsStep(action))
                condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle());
        }

        return new PlayResult(CraftSimulator.IsSuccess(parameters, state), state.Quality, used, skipped, state);
    }
}
