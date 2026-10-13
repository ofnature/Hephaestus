using System;
using System.Collections.Generic;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Solver;

/// <summary>
/// Plays whole synthesis attempts with the <see cref="LiveEvaluator"/> choosing every action, under
/// the dice the game rolls. Its summary is directly comparable with
/// <see cref="RotationPlayer.Sample"/> for the same recipe and baseline: that pair of numbers is
/// the measurement the evaluator's weights are tuned against — measure, don't eyeball.
/// </summary>
public static class PolicyPlayer
{
    private const int MaxSteps = 120;

    public static PlayResult Play(
        in CraftParameters parameters, IReadOnlyList<CraftAction> baseline, ushort conditionsFlag, Random random, EvaluatorSettings? settings = null)
    {
        var odds = ConditionOdds.ForRecipe(conditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(conditionsFlag);
        var evaluator = new LiveEvaluator(parameters, baseline, conditionsFlag, settings);

        var state = CraftSimulator.Initial(parameters);
        var condition = Condition.Normal; // every synthesis opens on Normal
        var used = 0;

        while (used < MaxSteps && !CraftSimulator.IsFinal(parameters, state))
        {
            if (evaluator.Recommend(state, condition).Action is not { } action)
                break;

            var success = random.Next(100) < CraftSimulator.SuccessPercent(action, condition);
            if (CraftSimulator.Use(parameters, state, action, condition, success, out var next) != ActionError.None)
                break; // nothing usable — the evaluator has nowhere left to go

            state = next;
            used++;
            evaluator.Observe(action);
            if (CraftSimulator.SpendsStep(action))
                condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle());
        }

        return new PlayResult(CraftSimulator.IsSuccess(parameters, state), state.Quality, used, 0, state);
    }

    /// <summary>Many attempts, summarised. Deterministic for a seed as long as the settings' budget is unlimited.</summary>
    public static PlaySummary Sample(
        in CraftParameters parameters, IReadOnlyList<CraftAction> baseline, ushort conditionsFlag, int runs, int seed, EvaluatorSettings? settings = null)
    {
        var random = new Random(seed);
        var completed = 0;
        long qualitySum = 0;
        var min = int.MaxValue;
        var max = int.MinValue;

        for (var i = 0; i < runs; i++)
        {
            var result = Play(parameters, baseline, conditionsFlag, random, settings);
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
}
