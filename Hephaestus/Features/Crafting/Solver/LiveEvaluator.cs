using System;
using System.Collections.Generic;
using System.Diagnostics;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Solver;

/// <summary>Knobs for the live evaluator. Defaults are the scoping decisions; the weights are for the Monte Carlo harness to tune.</summary>
public sealed record EvaluatorSettings
{
    /// <summary>Hard cap for the iterative deepening.</summary>
    public int MaxDepth { get; init; } = 4;

    /// <summary>Wall-clock budget per decision. The deepest depth that FINISHED inside it is what gets used.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How many plies branch on every possible next condition; deeper plies assume Normal.</summary>
    public int ConditionBranchPlies { get; init; } = 2;

    /// <summary>Soft baseline-death rule — see <see cref="BaselineTracker.Assess"/>.</summary>
    public float DeviationPercent { get; init; } = 25f;

    /// <summary>Quality worth aiming for; anything beyond it scores nothing. 0 = the recipe's maximum.</summary>
    public int QualityGoal { get; init; }

    /// <summary>
    /// Worth of finishing the craft. It dwarfs <see cref="QualityWeight"/> on purpose: outcomes are
    /// averaged over the dice, so a line with even a small chance of breaking the item loses more
    /// here than any quality it could gain. This IS the safety margin.
    /// </summary>
    public double CompletionWeight { get; init; } = 1_000_000;

    /// <summary>Worth of reaching the quality goal (scaled linearly below it).</summary>
    public double QualityWeight { get; init; } = 100_000;

    /// <summary>Worth of a full CP bar left over — a tie-breaker.</summary>
    public double CpWeight { get; init; } = 100;

    /// <summary>Cost per step taken — a tie-breaker that prefers the shorter of two equal crafts.</summary>
    public double StepWeight { get; init; } = 10;
}

/// <param name="Action">What to press now; null when nothing can be used (the synthesis is over).</param>
/// <param name="Preview">The steps expected to follow IF conditions stay Normal — tentative by nature.</param>
/// <param name="Depth">Deepest search depth that completed inside the budget.</param>
/// <param name="FollowsBaseline">The action is the baseline's own next step.</param>
/// <param name="Reason">One line for the status display: following the plan, or why not.</param>
public readonly record struct Recommendation(
    CraftAction? Action,
    IReadOnlyList<CraftAction> Preview,
    double Score,
    int Depth,
    BaselineStatus Baseline,
    bool FollowsBaseline,
    string Reason);

/// <summary>
/// The live half of the hybrid solver. After every real action it re-scores every legal next
/// action against the REAL state and the condition that actually rolled, by a shallow expectimax
/// whose leaves are closed out by the baseline's remaining steps and then the <see cref="Finisher"/>.
/// It follows the baseline whenever nothing beats it, and leaves it whenever something does.
///
/// Pure CPU over value-type states: run <see cref="Recommend"/> on a background thread with an
/// immutable snapshot. One instance per craft; do not call its methods concurrently.
/// </summary>
public sealed class LiveEvaluator
{
    private static readonly CraftAction[] AllActions = Enum.GetValues<CraftAction>();

    private readonly CraftParameters _parameters;
    private readonly EvaluatorSettings _settings;
    private readonly BaselineTracker _baseline;
    private readonly float[][] _nextOdds; // [current condition] -> odds of each next condition
    private readonly int _qualityGoal;

    // Per-search state.
    private CraftAction[] _remaining = Array.Empty<CraftAction>();
    private long _deadline;
    private bool _aborted;

    public LiveEvaluator(
        in CraftParameters parameters, IReadOnlyList<CraftAction> baseline, ushort conditionsFlag, EvaluatorSettings? settings = null)
    {
        _parameters = parameters;
        _settings = settings ?? new EvaluatorSettings();
        _baseline = new BaselineTracker(parameters, baseline, _settings.DeviationPercent);
        _qualityGoal = _settings.QualityGoal > 0 ? Math.Min(_settings.QualityGoal, parameters.MaxQuality) : parameters.MaxQuality;

        var rollOdds = ConditionOdds.ForRecipe(conditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(conditionsFlag);
        _nextOdds = new float[ConditionOdds.ConditionCount][];
        for (var current = 0; current < ConditionOdds.ConditionCount; current++)
        {
            // A forced transition answers the same whatever the roll; a free roll gives the table.
            var low = ConditionOdds.Next(rollOdds, expert, (Condition)current, 0f);
            var high = ConditionOdds.Next(rollOdds, expert, (Condition)current, 0.999999f);
            var forced = low == high && rollOdds[(int)low] < 0.999f;
            if (forced)
            {
                _nextOdds[current] = new float[ConditionOdds.ConditionCount];
                _nextOdds[current][(int)low] = 1f;
            }
            else
            {
                _nextOdds[current] = rollOdds;
            }
        }
    }

    public BaselineStatus Baseline => _baseline.Status;

    /// <summary>Tell the evaluator what was REALLY executed (whether or not it recommended it).</summary>
    public void Observe(CraftAction executed) => _baseline.Observe(executed);

    /// <param name="excluded">
    /// <see cref="ActionMask"/> bits never to recommend as the next action — for actions the game itself
    /// keeps refusing, whatever the simulator thinks.
    /// </param>
    public Recommendation Recommend(in CraftState state, Condition condition, ulong excluded = 0)
    {
        _baseline.Assess(state);
        _remaining = _baseline.RemainingSnapshot();
        var planned = _baseline.Next;

        _deadline = _settings.Budget == TimeSpan.MaxValue
            ? long.MaxValue
            : Stopwatch.GetTimestamp() + (long)(_settings.Budget.TotalSeconds * Stopwatch.Frequency);

        var candidates = Candidates(state, condition, planned, excluded);
        if (candidates.Count == 0)
            return new Recommendation(null, Array.Empty<CraftAction>(), 0, 0, _baseline.Status, false, "No usable action — the synthesis is over.");

        var best = candidates[0];
        var bestScore = double.NegativeInfinity;
        var plannedScore = double.NaN;
        var depthReached = 0;

        for (var depth = 1; depth <= Math.Max(1, _settings.MaxDepth); depth++)
        {
            _aborted = false;
            var roundBest = candidates[0];
            var roundScore = double.NegativeInfinity;
            var roundPlanned = double.NaN;

            foreach (var action in candidates)
            {
                var score = ActionValue(state, condition, 0UL, action, depth, ply: 0, steps: 0);
                if (_aborted)
                    break;

                if (action == planned)
                    roundPlanned = score;
                if (score > roundScore) // strict: on a tie the earlier candidate wins, and the planned step is first
                    (roundBest, roundScore) = (action, score);
            }

            if (_aborted && depth > 1)
                break; // keep the last depth that finished

            (best, bestScore, plannedScore, depthReached) = (roundBest, roundScore, roundPlanned, depth);
            if (_aborted)
                break;
        }

        var follows = planned is { } p && best == p;
        return new Recommendation(
            best, Preview(state, condition, best), bestScore, depthReached, _baseline.Status, follows,
            Explain(best, planned, condition, bestScore, plannedScore, follows));
    }

    // ---- search ------------------------------------------------------------------------------------

    private double Value(in CraftState state, Condition condition, ulong consumed, int depth, int ply, int steps)
    {
        if (depth == 0)
            return Leaf(state, consumed, steps);

        var best = double.NegativeInfinity;
        foreach (var action in AllActions)
        {
            if (CraftSimulator.Check(_parameters, state, action, condition) != ActionError.None || !Worthwhile(state, action))
                continue;

            var score = ActionValue(state, condition, consumed, action, depth, ply, steps);
            if (score > best)
                best = score;
            if (_aborted)
                return best;
        }

        return double.IsNegativeInfinity(best) ? Leaf(state, consumed, steps) : best;
    }

    private double ActionValue(in CraftState state, Condition condition, ulong consumed, CraftAction action, int depth, int ply, int steps)
    {
        var percent = CraftSimulator.SuccessPercent(action, condition);
        if (percent >= 100)
            return Outcome(state, condition, consumed, action, true, depth, ply, steps);

        var chance = percent / 100.0;
        return chance * Outcome(state, condition, consumed, action, true, depth, ply, steps)
               + (1 - chance) * Outcome(state, condition, consumed, action, false, depth, ply, steps);
    }

    private double Outcome(
        in CraftState state, Condition condition, ulong consumed, CraftAction action, bool success, int depth, int ply, int steps)
    {
        if (CraftSimulator.Use(_parameters, state, action, condition, success, out var next) != ActionError.None)
            return double.NegativeInfinity;

        if (CraftSimulator.IsFinal(_parameters, next))
            return Utility(next, steps + 1);

        var nextConsumed = BaselineTracker.Consume(_remaining, consumed, action);

        if (Stopwatch.GetTimestamp() > _deadline)
        {
            _aborted = true;
            return Leaf(next, nextConsumed, steps + 1);
        }

        // Instant actions roll no new condition.
        if (!CraftSimulator.SpendsStep(action))
            return Value(next, condition, nextConsumed, depth - 1, ply + 1, steps + 1);

        var odds = _nextOdds[(int)condition];
        if (ply >= _settings.ConditionBranchPlies)
        {
            // Past the branching plies assume Normal — but never ignore a FORCED transition
            // (the Poor after an Excellent is a certainty, not a risk).
            var assumed = ForcedOrNormal(odds);
            return Value(next, assumed, nextConsumed, depth - 1, ply + 1, steps + 1);
        }

        var expected = 0.0;
        for (var i = 0; i < odds.Length; i++)
        {
            if (odds[i] <= 0f)
                continue;
            expected += odds[i] * Value(next, (Condition)i, nextConsumed, depth - 1, ply + 1, steps + 1);
        }

        return expected;
    }

    private static Condition ForcedOrNormal(float[] odds)
    {
        for (var i = 0; i < odds.Length; i++)
        {
            if (odds[i] >= 0.999f)
                return (Condition)i;
        }

        return Condition.Normal;
    }

    /// <summary>Close the craft out: the baseline's unconsumed steps, then the finisher. If that breaks the item, the finisher alone.</summary>
    private double Leaf(in CraftState state, ulong consumed, int steps)
    {
        if (_remaining.Length > 0)
        {
            var replayed = BaselineTracker.Replay(_parameters, state, _remaining, consumed, _qualityGoal, out var replaySteps);
            var closed = Finisher.Run(_parameters, replayed, out var closingSteps);
            if (CraftSimulator.IsSuccess(_parameters, closed))
                return Utility(closed, steps + replaySteps + closingSteps);
        }

        var finished = Finisher.Run(_parameters, state, out var finisherSteps);
        return Utility(finished, steps + finisherSteps);
    }

    private double Utility(in CraftState final, int steps)
    {
        if (!CraftSimulator.IsSuccess(_parameters, final))
            return 1000.0 * final.Progress / Math.Max(1, _parameters.MaxProgress); // among failures, prefer the nearer miss

        var quality = Math.Min(final.Quality, _qualityGoal) / (double)Math.Max(1, _qualityGoal);
        return _settings.CompletionWeight
               + _settings.QualityWeight * quality
               + _settings.CpWeight * final.Cp / Math.Max(1, _parameters.MaxCp)
               - _settings.StepWeight * steps;
    }

    // ---- candidates --------------------------------------------------------------------------------

    /// <summary>Legal, non-wasteful actions, with the baseline's next step first so that it wins ties.</summary>
    private List<CraftAction> Candidates(in CraftState state, Condition condition, CraftAction? planned, ulong excluded)
    {
        var list = new List<CraftAction>(AllActions.Length);
        if (planned is { } p && !ActionMask.Has(excluded, p)
            && CraftSimulator.Check(_parameters, state, p, condition) == ActionError.None)
            list.Add(p);

        foreach (var action in AllActions)
        {
            if (action == planned || ActionMask.Has(excluded, action))
                continue;
            if (CraftSimulator.Check(_parameters, state, action, condition) == ActionError.None && Worthwhile(state, action))
                list.Add(action);
        }

        return list;
    }

    /// <summary>Prune moves that are legal but never right, to keep the branching factor down.</summary>
    private bool Worthwhile(in CraftState state, CraftAction action)
    {
        if (state.Quality >= _qualityGoal && BaselineTracker.IsQualityOnly(action))
            return false;

        var missing = _parameters.MaxDurability - state.Durability;
        return action switch
        {
            CraftAction.MasterMend => missing >= 30,
            CraftAction.ImmaculateMend => missing >= 45,
            CraftAction.Manipulation => state.Manipulation <= 1,
            CraftAction.WasteNot or CraftAction.WasteNot2 => state.WasteNot <= 1,
            CraftAction.Innovation => state.Innovation <= 1,
            CraftAction.Veneration => state.Veneration <= 1,
            CraftAction.GreatStrides => state.GreatStrides == 0,
            CraftAction.TricksOfTheTrade => state.Cp < _parameters.MaxCp,
            _ => true,
        };
    }

    // ---- presentation ------------------------------------------------------------------------------

    private IReadOnlyList<CraftAction> Preview(in CraftState state, Condition condition, CraftAction chosen)
    {
        const int Length = 3;
        var preview = new List<CraftAction>(Length);
        if (CraftSimulator.Use(_parameters, state, chosen, condition, true, out var next) != ActionError.None
            || CraftSimulator.IsFinal(_parameters, next))
            return preview;

        var consumed = BaselineTracker.Consume(_remaining, 0UL, chosen);
        for (var i = 0; i < _remaining.Length && preview.Count < Length; i++)
        {
            if (i < 64 && (consumed & (1UL << i)) != 0)
                continue;
            if (CraftSimulator.Use(_parameters, next, _remaining[i], Condition.Normal, true, out var after) != ActionError.None)
                continue;

            preview.Add(_remaining[i]);
            next = after;
            if (CraftSimulator.IsFinal(_parameters, next))
                return preview;
        }

        if (preview.Count < Length)
        {
            var closing = new List<CraftAction>();
            Finisher.Run(_parameters, next, out _, closing);
            for (var i = 0; i < closing.Count && preview.Count < Length; i++)
                preview.Add(closing[i]);
        }

        return preview;
    }

    private string Explain(CraftAction best, CraftAction? planned, Condition condition, double bestScore, double plannedScore, bool follows)
    {
        if (follows)
            return "Following the baseline.";

        if (planned is not { } p)
        {
            return _baseline.Status switch
            {
                BaselineStatus.CannotComplete => $"{best}: baseline dropped (its remaining steps can no longer finish the craft).",
                BaselineStatus.Drifted => $"{best}: baseline dropped (fell more than {_settings.DeviationPercent:0}% behind its plan).",
                _ => $"{best}: baseline used up, finishing alone.",
            };
        }

        if (double.IsNaN(plannedScore))
            return $"{best}: the planned {p} cannot be used right now ({condition}).";

        // Express the gain in what it is mostly made of, so the line means something to a person.
        var gain = bestScore - plannedScore;
        return gain >= _settings.CompletionWeight / 2
            ? $"{best} instead of {p}: the plan would risk the craft from here ({condition})."
            : $"{best} instead of {p} on {condition}: worth +{gain / _settings.QualityWeight * 100:0.0}% quality.";
    }
}
