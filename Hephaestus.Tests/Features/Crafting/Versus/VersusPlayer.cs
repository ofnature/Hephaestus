using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Tests.Features.Crafting.Versus;

/// <summary>What a driver wants pressed: one of the simulator's actions, or Final Appraisal.</summary>
internal readonly record struct VersusMove(CraftAction Action, bool FinalAppraisal = false)
{
    public static readonly VersusMove Appraise = new(default, FinalAppraisal: true);

    public string Name => FinalAppraisal ? "FinalAppraisal" : Action.ToString();
}

/// <summary>One action as it was played: the condition it was taken under, and whether its roll passed.</summary>
internal readonly record struct VersusStep(Condition Condition, string Action, bool Success);

/// <param name="Stalled">Why the craft stopped with neither a finished nor a broken item; null when it ended normally.</param>
internal sealed record VersusResult(bool Completed, int Quality, int Steps, CraftState Final, IReadOnlyList<VersusStep> Trace, string? Stalled)
{
    /// <summary>Finished AND at least the quality the recipe demands (0 = none demanded).</summary>
    public bool Delivered(int requiredQuality) => Completed && Quality >= requiredQuality;
}

/// <summary>Everything about a craft in progress that the shared world owns.</summary>
internal struct VersusWorld
{
    public CraftState State;
    public Condition Condition;

    /// <summary>Steps of Final Appraisal left; 0 = not up.</summary>
    public int Appraisal;
}

/// <summary>Chooses every action of one synthesis. One instance per craft.</summary>
internal interface IVersusDriver
{
    /// <returns>What to press now; null when it has nothing to press.</returns>
    VersusMove? Next(in CraftState state, Condition condition, int finalAppraisalLeft);

    /// <summary>What was really executed, the condition it was taken under, and whether its roll passed.</summary>
    void Observed(VersusMove move, Condition takenUnder, bool success);
}

/// <summary>
/// ONE craft, played by whoever is given the wheel, in the world both sides share: Hephaestus's
/// simulator (pinned to raphael-sim's in-game-verified vectors) and the dice of
/// <see cref="PolicyPlayer"/> — per step, first the success roll, then the condition roll.
///
/// Why the dice make this a one-to-one comparison: the next condition never depends on what was
/// pressed, only on the condition before it. Both sides draw exactly two numbers per step, so on
/// the same seed they meet THE SAME CONDITION ON THE SAME STEP NUMBER for as long as either craft
/// lasts — the same material, worked by two different crafters. (True without specialist actions,
/// which neither side is given here: they would take an action without taking a step.)
///
/// Final Appraisal is not one of the simulator's actions — Hephaestus never presses it — but
/// Artisan's solvers lean on it, so the world gives it to whoever asks, by Artisan's own simulator's
/// rules: 1 CP, no step, 5 steps (7 on Primed), and while it is up an action that would finish the
/// craft leaves it one point short instead and spends the buff.
/// </summary>
internal static class VersusPlayer
{
    private const int MaxMoves = 160;
    private const int FinalAppraisalLevel = 42;

    public static VersusResult Play(
        in CraftParameters parameters, ushort conditionsFlag, int initialQuality, int seed, IVersusDriver driver)
    {
        var random = new Random(seed);
        var odds = ConditionOdds.ForRecipe(conditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(conditionsFlag);

        var world = new VersusWorld { State = CraftSimulator.Initial(parameters), Condition = Condition.Normal }; // every synthesis opens on Normal
        world.State.Quality = Math.Min(parameters.MaxQuality, initialQuality);
        var steps = 0;
        var trace = new List<VersusStep>();
        string? stalled = null;

        while (!CraftSimulator.IsFinal(parameters, world.State))
        {
            if (trace.Count >= MaxMoves)
            {
                stalled = $"still going after {MaxMoves} actions";
                break;
            }

            if (driver.Next(world.State, world.Condition, world.Appraisal) is not { } move)
            {
                stalled = "nothing to press";
                break;
            }

            var takenUnder = world.Condition;
            stalled = Apply(parameters, odds, expert, ref world, move, random, out var success);
            if (stalled != null)
                break;

            if (!move.FinalAppraisal)
                steps++;
            trace.Add(new VersusStep(takenUnder, move.Name, success));
            driver.Observed(move, takenUnder, success);
        }

        return new VersusResult(CraftSimulator.IsSuccess(parameters, world.State), world.State.Quality, steps, world.State, trace, stalled);
    }

    /// <summary>
    /// One action in the shared world, dice included: the success roll first, then - if the action took
    /// a step - the condition roll. Final Appraisal takes no step and rolls nothing.
    /// </summary>
    /// <returns>Null when the action was taken; otherwise why it could not be, and nothing changed.</returns>
    public static string? Apply(
        in CraftParameters parameters, float[] odds, bool expert, ref VersusWorld world, VersusMove move, Random random, out bool success)
    {
        success = true;
        if (move.FinalAppraisal)
        {
            if (parameters.JobLevel < FinalAppraisalLevel || world.State.Cp < 1)
                return "FinalAppraisal could not be used";

            world.State.Cp -= 1;
            world.Appraisal = 5 + (world.Condition == Condition.Primed ? 2 : 0);
            if (world.State.Combo != Combo.SynthesisBegin)
                world.State.Combo = Combo.None; // instant actions drop the touch combo, as the simulator's own do
            return null;
        }

        var action = move.Action;
        success = random.Next(100) < CraftSimulator.SuccessPercent(action, world.Condition);

        // Under Final Appraisal the craft cannot finish: let the step run as if the bar were endless, then hold it one short.
        var rules = world.Appraisal > 0 ? parameters with { MaxProgress = int.MaxValue / 4 } : parameters;
        var error = CraftSimulator.Use(rules, world.State, action, world.Condition, success, out var next);
        if (error != ActionError.None)
            return $"{action} could not be used ({error})";

        if (world.Appraisal > 0)
        {
            if (next.Progress >= parameters.MaxProgress)
            {
                next.Progress = parameters.MaxProgress - 1;
                world.Appraisal = 0;
            }
            else if (CraftSimulator.SpendsStep(action))
            {
                world.Appraisal--;
            }
        }

        world.State = next;
        if (CraftSimulator.SpendsStep(action))
            world.Condition = ConditionOdds.Next(odds, expert, world.Condition, random.NextSingle());
        return null;
    }

    /// <summary>
    /// The conditions a seed deals, by step number, whoever plays — the dice protocol above, replayed
    /// with nobody at the wheel. Valid for crafts made of step-taking actions with a success roll each.
    /// </summary>
    public static Condition[] Conditions(ushort conditionsFlag, int jobLevel, int seed, int steps)
    {
        var random = new Random(seed);
        var odds = ConditionOdds.ForRecipe(conditionsFlag, jobLevel);
        var expert = ConditionOdds.IsExpert(conditionsFlag);
        var conditions = new Condition[steps];
        var condition = Condition.Normal;
        for (var i = 0; i < steps; i++)
        {
            conditions[i] = condition;
            random.Next(100);
            condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle());
        }

        return conditions;
    }
}

/// <summary>Hephaestus at the wheel: the live evaluator, exactly as <see cref="PolicyPlayer"/> drives it.</summary>
internal sealed class HephaestusDriver : IVersusDriver
{
    private readonly LiveEvaluator _evaluator;

    public HephaestusDriver(in CraftParameters parameters, IReadOnlyList<CraftAction> baseline, ushort conditionsFlag, EvaluatorSettings settings)
        => _evaluator = new LiveEvaluator(parameters, baseline, conditionsFlag, settings);

    public VersusMove? Next(in CraftState state, Condition condition, int finalAppraisalLeft)
        => _evaluator.Recommend(state, condition).Action is { } action ? new VersusMove(action) : null;

    public void Observed(VersusMove move, Condition takenUnder, bool success) => _evaluator.Observe(move.Action);
}
