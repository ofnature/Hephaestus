using System;
using Hephaestus.Features.Crafting.Simulation;
namespace Hephaestus.Features.Crafting.Expert;

/// <summary>One craft for the expert solver: the simulator's parameters, the conditions the recipe deals, and what the item must reach.</summary>
/// <param name="Target">The quality that counts as delivered: the recipe's demand, a scored collectable's top rating, else full quality.</param>
/// <param name="Tiers">A Cosmic mission collectable's three ratings as quality, lowest first; null = an item with one demand.</param>
/// <param name="Duty">The Cosmic duty action the mission lends (allowed in <see cref="Parameters"/>); uses are handed in per craft.</param>
/// <param name="TimeValue">
/// What a second is worth on this craft, in points (a delivered item is worth 1,000; see <see cref="ExpertSearch.Value"/>).
/// It is a matter of what the craft is FOR (user, 2026-11-11): 0 for a one-off or a high-quality attempt - quality first,
/// time hardly matters; a real price for bulk crafting, where time decides; the mission's own rate on a timed mission.
/// </param>
/// <param name="ByHqChance">
/// An ordinary item that can be high quality and has no demand: what it is worth is its CHANCE of coming out high
/// quality (<see cref="HqChance"/>), not "full quality or nothing" - nine tenths of the bar is an 86% item, not a failure.
/// </param>
public sealed record ExpertProblem(
    string Name, CraftParameters Parameters, ushort ConditionsFlag, int InitialQuality, int Target, int[]? Tiers = null, CraftAction? Duty = null,
    double TimeValue = 0, bool ByHqChance = false)
{
    /// <summary>
    /// The quality the policy works towards: <see cref="Target"/>, except that an item judged by its HQ chance may be
    /// aimed lower (<see cref="Knob.Aim"/>) - where time has a price, the last percents of the bar can cost more than they add.
    /// </summary>
    public int Aim(ExpertTuning tuning)
        => ByHqChance ? Math.Min(Target, (int)((long)Parameters.MaxQuality * tuning[Knob.Aim] / 100)) : Target;

    /// <summary>
    /// What a Master mission pays for one item, before the mission's multiplier. PLAYER-REPORTED (Square Enix
    /// forum thread 529170), not in any sheet found: 100 at the first rating, 300 at the second, 700 at the
    /// third, then 20 for every percent of the maximum quality above 85, up to 1,000.
    /// </summary>
    public int Points(bool completed, int quality)
    {
        if (!completed || Tiers is null || quality < Tiers[0])
            return 0;
        if (quality < Tiers[1])
            return 100;
        if (quality < Tiers[2])
            return 300;

        var percent = (int)((long)quality * 100 / Parameters.MaxQuality);
        return Math.Min(1000, 700 + 20 * Math.Max(0, percent - 85));
    }
}

/// <param name="Quarters">Time the craft took, in quarter-seconds (<see cref="CraftSimulator.QuarterSeconds"/> of every action).</param>
/// <param name="DutyUsed">Uses of the mission's duty action spent.</param>
public readonly record struct ExpertRun(bool Completed, int Quality, int Steps, int Overruled, CraftState Final, int Quarters = 0, int DutyUsed = 0)
{
    public bool Delivered(int target) => Completed && Quality >= target;
}

/// <summary>
/// Plays whole SIMULATED crafts with <see cref="ExpertPolicy"/>, alone or under <see cref="ExpertSearch"/>, as
/// fast as the simulator goes: no trace, no allocation per step. It is what <see cref="ExpertTuner"/> turns the
/// knobs by, in the plugin (before a craft) and in the tests' sandbox. The dice are the tests' shared world's
/// (per action: the success roll, then - if it took a step - the condition roll), so seed N here is seed N
/// there (<c>TheFastLoop_PlaysTheSameCraftAsTheSharedWorld</c> pins it; true for crafts without a duty action).
/// </summary>
public static class ExpertPlayer
{
    private const int MaxActions = 200;

    /// <param name="overrules">Counts, per "condition: planned > pressed", the decisions the search took out of the policy's hands.</param>
    /// <param name="dutyUses">Uses of <see cref="ExpertProblem.Duty"/> the mission hands this craft.</param>
    public static ExpertRun Play(
        ExpertProblem problem, ExpertTuning tuning, int seed, SearchSettings? search = null,
        System.Collections.Concurrent.ConcurrentDictionary<string, int>? overrules = null, int dutyUses = 0)
    {
        var parameters = problem.Parameters;
        var random = new Random(seed);
        var odds = ConditionOdds.ForRecipe(problem.ConditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(problem.ConditionsFlag);
        var searchSeed = (ulong)seed * 0xD1B54A32D192ED03UL;

        var state = Start(problem, dutyUses);
        var condition = Condition.Normal;
        var steps = 0;
        var overruled = 0;
        var quarters = 0;

        for (var actions = 0; actions < MaxActions && !CraftSimulator.IsFinal(parameters, state); actions++)
        {
            CraftAction action;
            if (search is null)
            {
                action = ExpertPolicy.Decide(parameters, tuning, problem.Aim(tuning), state, condition, problem.Tiers);
            }
            else
            {
                action = ExpertSearch.Choose(problem, tuning, odds, expert, state, condition, search, ref searchSeed, out var planned);
                if (action != planned)
                {
                    overruled++;
                    overrules?.AddOrUpdate($"{condition}: {planned} > {action}", 1, (_, count) => count + 1);
                }
            }

            var success = random.Next(100) < CraftSimulator.SuccessPercent(state, action, condition);
            if (CraftSimulator.Use(parameters, state, action, condition, success, out var next) != ActionError.None)
                break;

            quarters += CraftSimulator.QuarterSeconds(action);
            state = next;
            if (CraftSimulator.SpendsStep(action))
            {
                steps++;
                condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle(), state.MaterialMiracle > 0);
            }
        }

        var used = dutyUses - state.SteadyHandCharges - state.MaterialMiracleCharges;
        return new ExpertRun(CraftSimulator.IsSuccess(parameters, state), state.Quality, steps, overruled, state, quarters, used);
    }

    /// <summary>The craft before its first action, with the uses of the duty action the mission hands it.</summary>
    public static CraftState Start(ExpertProblem problem, int dutyUses)
    {
        var state = CraftSimulator.Initial(problem.Parameters);
        state.Quality = Math.Min(problem.Parameters.MaxQuality, problem.InitialQuality);
        if (problem.Duty == CraftAction.StellarSteadyHand)
            state.SteadyHandCharges = (byte)dutyUses;
        else if (problem.Duty == CraftAction.MaterialMiracle)
            state.MaterialMiracleCharges = (byte)dutyUses;
        return state;
    }
}
