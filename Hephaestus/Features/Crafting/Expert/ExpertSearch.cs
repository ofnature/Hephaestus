using System;
using System.Collections.Generic;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Expert;

/// <param name="Samples">Futures played per candidate action, at most.</param>
/// <param name="Margin">How much better, in <see cref="ExpertSearch.Value"/>, an action must look before the policy is overruled.</param>
/// <param name="Confidence">...and by how many standard errors of the paired difference. Sampling noise is not a reason.</param>
/// <param name="TimeValue">
/// Scored collectables only: what a second is worth, in points - the points per second the crafter makes anyway,
/// so a future is judged by its points MINUS the time it took at that rate. 0 = time is free.
/// </param>
public sealed record SearchSettings(int Samples = 96, double Margin = 0.03, double Confidence = 2.5, double TimeValue = 0);

/// <summary>Rolls for the sampled futures: small, fast, and a value - copying it replays the same luck.</summary>
public struct Dice
{
    private ulong _state;

    public Dice(ulong seed) => _state = seed;

    public int Percent() => (int)(Next() % 100);

    public float Unit() => (Next() >> 40) * (1f / (1 << 24));

    private ulong Next() // splitmix64
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>
/// The live half for hard recipes: before each action, ask of every
/// legal one "what if I pressed this now and played <see cref="ExpertPolicy"/> from there on?", over
/// the same sampled futures (rolled conditions, actions that can fail), and overrule the policy only
/// where an action is better by more than the noise of the PAIRED differences - the same futures for
/// every candidate, so most of the luck cancels. (Without that test, taking the best average of twenty
/// noisy ones is worse than not looking at all: measured, 2026-11-11.)
///
/// The same shape as the plugin's live solver - a reference to follow, re-scored against the real
/// state - with a policy as the reference instead of a fixed rotation, and delivery as the score.
/// </summary>
public static class ExpertSearch
{
    private const int MaxSteps = 120;
    private const int Batch = 16;

    private static readonly CraftAction[] Actions = Enum.GetValues<CraftAction>();

    /// <summary>What a finished future is worth: 1 = delivered at the demanded quality; a miss is worth little, a broken item less.</summary>
    public static double Score(in CraftParameters parameters, int target, in CraftState end)
    {
        var completed = CraftSimulator.IsSuccess(parameters, end);
        if (completed && end.Quality >= target)
            return 1.0;

        var quality = Math.Min(1.0, end.Quality / (double)Math.Max(1, target));
        return (completed ? 0.25 : 0.1) * quality;
    }

    /// <summary>
    /// What an ordinary item is worth when it is judged by its chance of being high quality: a quarter for being
    /// finished at all (the materials became an item), the rest by the game's HQ chance at the quality it ended on.
    /// A broken item is worth nothing - never less time than it would have taken to finish it.
    /// </summary>
    public static double HqWorth(in CraftParameters parameters, in CraftState end)
        => CraftSimulator.IsSuccess(parameters, end) ? 0.25 + 0.75 * HqChance.Percent(end.Quality, parameters.MaxQuality) / 100.0 : 0.0;

    /// <summary>
    /// What an item is worth, less the time it took at <paramref name="timeValue"/> points a second - in thousands
    /// of points, so 1.0 is "a perfect item, for free". An item with one demand is worth <see cref="Score"/>
    /// (1,000 points delivered); a scored collectable is worth its points.
    /// </summary>
    /// <param name="quarterSeconds">Time spent on the craft (or on the future, from where it was sampled).</param>
    /// <param name="timeValue">0 = time is free: the item alone counts (every expert recipe with a demand, as tuned).</param>
    public static double Value(ExpertProblem problem, in CraftState end, int quarterSeconds, double timeValue)
    {
        if (problem.Tiers is null)
        {
            var worth = problem.ByHqChance ? HqWorth(problem.Parameters, end) : Score(problem.Parameters, problem.Target, end);
            return worth - timeValue * quarterSeconds / 4000.0;
        }

        var points = problem.Points(CraftSimulator.IsSuccess(problem.Parameters, end), end.Quality);
        return (points - timeValue * quarterSeconds / 4.0) / 1000.0;
    }

    /// <param name="seed">Advanced by every call, so no two decisions sample the same futures.</param>
    /// <param name="planned">What the policy alone would have pressed; the answer differs when the search overruled it.</param>
    /// <param name="deadline">
    /// A <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> past which no further futures are sampled (the
    /// first two batches always are); 0 = none. Simulations never pass one, so they stay repeatable.
    /// </param>
    public static CraftAction Choose(
        ExpertProblem problem, ExpertTuning tuning, float[] odds, bool expert,
        in CraftState state, Condition condition, SearchSettings settings, ref ulong seed, out CraftAction planned, long deadline = 0)
    {
        var parameters = problem.Parameters;
        planned = ExpertPolicy.Decide(parameters, tuning, problem.Aim(tuning), state, condition, problem.Tiers);

        // Past this, quality work earns nothing more.
        var enough = problem.Tiers is null ? problem.Target : parameters.MaxQuality;
        var candidates = new List<CraftAction>(Actions.Length);
        foreach (var action in Actions)
        {
            if (action == planned || CraftSimulator.Check(parameters, state, action, condition) != ActionError.None)
                continue;
            if (state.Quality >= enough && CraftSimulator.ProgressGain(parameters, state, action, condition) == 0 && IsQualityWork(action))
                continue;
            candidates.Add(action);
        }

        if (candidates.Count == 0)
            return planned;

        var alive = new bool[candidates.Count];
        Array.Fill(alive, true);
        var sum = new double[candidates.Count];
        var squares = new double[candidates.Count];
        var played = 0;
        var remaining = candidates.Count;

        while (played < settings.Samples && remaining > 0)
        {
            for (var b = 0; b < Batch; b++)
            {
                var dice = new Dice(seed += 0x9E3779B97F4A7C15UL);
                var reference = Rollout(problem, tuning, odds, expert, state, condition, planned, dice, settings.TimeValue);
                for (var c = 0; c < candidates.Count; c++)
                {
                    if (!alive[c])
                        continue;

                    var difference = Rollout(problem, tuning, odds, expert, state, condition, candidates[c], dice, settings.TimeValue) - reference;
                    sum[c] += difference;
                    squares[c] += difference * difference;
                }
            }

            played += Batch;
            if (played < 2 * Batch)
                continue;
            if (deadline != 0 && System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                remaining = 0; // out of time: decide on what has been played

            // Stop paying for candidates that are clearly no better than the policy's own move.
            for (var c = 0; c < candidates.Count; c++)
            {
                if (alive[c] && sum[c] / played + 2.0 * StandardError(sum[c], squares[c], played) <= 0)
                {
                    alive[c] = false;
                    remaining--;
                }
            }
        }

        var choice = planned;
        var bestGain = 0.0;
        for (var c = 0; c < candidates.Count; c++)
        {
            if (!alive[c])
                continue;

            var gain = sum[c] / played;
            if (gain > bestGain && gain > Math.Max(settings.Margin, settings.Confidence * StandardError(sum[c], squares[c], played)))
                (choice, bestGain) = (candidates[c], gain);
        }

        return choice;
    }

    /// <summary>
    /// About how many seconds the rest of the craft takes: <paramref name="first"/>, then the policy, averaged over
    /// <paramref name="samples"/> sampled futures, on the simulator's clock. For the display - nothing is decided by it.
    /// </summary>
    public static double SecondsLeft(
        ExpertProblem problem, ExpertTuning tuning, float[] odds, bool expert,
        in CraftState state, Condition condition, CraftAction first, int samples, ulong seed)
    {
        var total = 0L;
        for (var i = 0; i < samples; i++)
        {
            Play(problem, tuning, odds, expert, state, condition, first, new Dice(seed += 0x9E3779B97F4A7C15UL), out var quarters);
            total += quarters;
        }

        return samples <= 0 ? -1 : total / (4.0 * samples);
    }

    /// <summary>Press <paramref name="first"/>, then let the policy play one sampled future to its end.</summary>
    public static double Rollout(
        ExpertProblem problem, ExpertTuning tuning, float[] odds, bool expert,
        CraftState state, Condition condition, CraftAction first, Dice dice, double timeValue = 0)
    {
        var end = Play(problem, tuning, odds, expert, state, condition, first, dice, out var quarters);
        return Value(problem, end, quarters, timeValue);
    }

    /// <summary>One sampled future: where it ends, and the time it took.</summary>
    private static CraftState Play(
        ExpertProblem problem, ExpertTuning tuning, float[] odds, bool expert,
        CraftState state, Condition condition, CraftAction first, Dice dice, out int quarters)
    {
        var parameters = problem.Parameters;
        var action = first;
        quarters = 0;
        for (var step = 0; step < MaxSteps; step++)
        {
            // Both rolls are drawn on every step, used or not: the futures of two candidates stay in step, condition for condition.
            var success = dice.Percent() < CraftSimulator.SuccessPercent(state, action, condition);
            var roll = dice.Unit();
            if (CraftSimulator.Use(parameters, state, action, condition, success, out var next) != ActionError.None)
                break;

            quarters += CraftSimulator.QuarterSeconds(action);
            state = next;
            if (CraftSimulator.IsFinal(parameters, state))
                break;
            if (CraftSimulator.SpendsStep(action))
                condition = ConditionOdds.Next(odds, expert, condition, roll, state.MaterialMiracle > 0);
            action = ExpertPolicy.Decide(parameters, tuning, problem.Aim(tuning), state, condition, problem.Tiers);
        }

        return state;
    }

    private static double StandardError(double sum, double squares, int count)
    {
        var mean = sum / count;
        var variance = Math.Max(0.0, (squares - count * mean * mean) / Math.Max(1, count - 1));
        return Math.Sqrt(variance / count);
    }

    private static bool IsQualityWork(CraftAction action) => action is CraftAction.BasicTouch or CraftAction.StandardTouch
        or CraftAction.AdvancedTouch or CraftAction.PrudentTouch or CraftAction.PreparatoryTouch or CraftAction.PreciseTouch
        or CraftAction.HastyTouch or CraftAction.DaringTouch or CraftAction.Reflect or CraftAction.TrainedFinesse
        or CraftAction.RefinedTouch or CraftAction.ByregotsBlessing or CraftAction.GreatStrides or CraftAction.Innovation
        or CraftAction.QuickInnovation;
}
