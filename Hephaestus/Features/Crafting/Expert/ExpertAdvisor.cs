using System;
using System.Diagnostics;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Features.Crafting.Expert;

/// <summary>
/// The expert solver at a real synthesis: <see cref="ExpertPolicy"/> names the action, and
/// <see cref="ExpertSearch"/> - when given - plays it and every other legal action over sampled
/// futures and overrules it only where the gain is clear of the noise. No plan and no memory beyond
/// how often the mission's duty action was used in this craft, so it can join a craft anywhere.
///
/// Pure CPU: <see cref="Recommend"/> runs on the session's background evaluation.
/// </summary>
public sealed class ExpertAdvisor : ICraftAdvisor
{
    private static readonly CraftAction[] AllActions = Enum.GetValues<CraftAction>();

    private readonly ExpertProblem _problem;
    private readonly Func<ExpertTuning> _tuning;
    private readonly SearchSettings? _search;
    private readonly TimeSpan _budget;
    private readonly float[] _odds;
    private readonly bool _expert;
    private ulong _seed;
    private int _dutyUsed;
    private int _firstCharges = -1;

    /// <param name="tuning">Read at every decision: the background tuning of this recipe may finish mid-craft.</param>
    /// <param name="search">Null = the policy alone.</param>
    /// <param name="budget">Wall-clock limit for one decision's search; zero = none (tests).</param>
    public ExpertAdvisor(ExpertProblem problem, Func<ExpertTuning> tuning, SearchSettings? search, TimeSpan budget, ulong seed = 0x9E3779B97F4A7C15UL)
    {
        _problem = problem;
        _tuning = tuning;
        _search = search;
        _budget = budget;
        _odds = ConditionOdds.ForRecipe(problem.ConditionsFlag, problem.Parameters.JobLevel);
        _expert = ConditionOdds.IsExpert(problem.ConditionsFlag);
        _seed = seed;
    }

    public void Observe(CraftAction executed)
    {
        if (executed == _problem.Duty)
            _dutyUsed++;
    }

    public Recommendation Recommend(in CraftState state, Condition condition, ulong excluded = 0)
    {
        // Actions the game keeps refusing are simply not this crafter's any more.
        var problem = excluded == 0
            ? _problem
            : _problem with { Parameters = _problem.Parameters with { AllowedActions = _problem.Parameters.AllowedActions & ~excluded } };
        var parameters = problem.Parameters;
        if (CraftSimulator.IsFinal(parameters, state))
            return Answer(null, false, "Nothing usable — the synthesis is over.");

        // The mission's uses are the MISSION's: one craft takes no more of them than the knob allows.
        // Uses are counted twice over - the actions seen, and the stock going down - because a press made by hand
        // may not come past the action hook.
        var tuning = _tuning();
        var now = state;
        var charges = state.SteadyHandCharges + state.MaterialMiracleCharges;
        if (_firstCharges < 0)
            _firstCharges = charges + _dutyUsed;
        var used = Math.Max(_dutyUsed, _firstCharges - charges);
        var lend = Math.Max(0, tuning[Knob.DutyUses] - used);
        now.SteadyHandCharges = (byte)Math.Min(now.SteadyHandCharges, lend);
        now.MaterialMiracleCharges = (byte)Math.Min(now.MaterialMiracleCharges, lend);

        CraftAction action;
        CraftAction planned;
        if (_search is null)
        {
            action = planned = ExpertPolicy.Decide(parameters, tuning, problem.Aim(tuning), now, condition, problem.Tiers);
        }
        else
        {
            var deadline = _budget > TimeSpan.Zero ? Stopwatch.GetTimestamp() + (long)(_budget.TotalSeconds * Stopwatch.Frequency) : 0;
            action = ExpertSearch.Choose(problem, tuning, _odds, _expert, now, condition, _search, ref _seed, out planned, deadline);
        }

        if (CraftSimulator.Check(parameters, now, action, condition) != ActionError.None)
        {
            // The policy's last resort can be an action the game has ruled out: take anything that still moves the item.
            if (Fallback(parameters, now, condition) is not { } other)
                return Answer(null, false, "Nothing usable is left to press.");
            return Answer(other, false, $"Expert solver: {other} — the only thing left");
        }

        // For the display: how long the rest of the item takes, over a handful of the futures the policy would play.
        var seconds = ExpertSearch.SecondsLeft(problem, tuning, _odds, _expert, now, condition, action, TimeSamples, _seed ^ 0xA5A5A5A5A5A5A5A5UL);
        return action == planned
            ? Answer(action, true, $"Expert solver: {action}", seconds)
            : Answer(action, false, $"Expert solver: {action} — looked ahead, better than {planned}", seconds);
    }

    /// <summary>Futures averaged for <see cref="Recommendation.SecondsLeft"/>: a display figure, not worth more.</summary>
    private const int TimeSamples = 24;

    private static CraftAction? Fallback(in CraftParameters parameters, in CraftState state, Condition condition)
    {
        CraftAction? usable = null;
        foreach (var action in AllActions)
        {
            if (CraftSimulator.Check(parameters, state, action, condition) != ActionError.None)
                continue;
            if (CraftSimulator.ProgressGain(parameters, state, action, condition) > 0)
                return action;
            usable ??= action;
        }

        return usable;
    }

    /// <summary>"On plan" in the overlay = the policy's own move; "off plan" = the search took it out of its hands.</summary>
    private Recommendation Answer(CraftAction? action, bool policy, string reason, double secondsLeft = -1)
        => new(action, Array.Empty<CraftAction>(), 0, _search?.Samples ?? 0, BaselineStatus.Alive, policy, reason, secondsLeft);
}
