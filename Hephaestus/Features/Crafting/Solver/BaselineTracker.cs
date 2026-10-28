using System;
using System.Collections.Generic;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Solver;

/// <summary>Why the baseline stopped being used, if it has.</summary>
public enum BaselineStatus
{
    Alive,

    /// <summary>Hard rule: its remaining steps, played from the real state, can no longer finish the craft.</summary>
    CannotComplete,

    /// <summary>Soft rule: CP, durability or progress fell further behind its plan than the configured %.</summary>
    Drifted,
}

/// <summary>
/// The Raphael rotation as a REFERENCE, not a rail: a list of steps still to come, which real
/// actions consume. A real action is one of three things relative to the plan —
///
///   - the planned step itself, or one planned a little later pulled forward (consumes that step);
///   - a replacement for the next step of the same kind, e.g. Precise Touch on a Good where Basic
///     Touch was planned (consumes the next step);
///   - an insertion the plan never had, e.g. Tricks of the Trade on a Good, or Observe to sit out
///     a Poor (consumes nothing — the plan resumes afterwards).
///
/// The same rule runs inside the lookahead (on a bit mask, no allocation) and on real actions.
/// </summary>
public sealed class BaselineTracker
{
    /// <summary>How far ahead an exact match may be pulled forward, counted in unconsumed steps.</summary>
    public const int PullForwardWindow = 3;

    private readonly CraftParameters _parameters;
    private readonly float _deviationPercent;
    private readonly List<CraftAction> _remaining;
    private readonly List<StepChange> _plannedChange; // what each remaining step did to the plan's own state

    // Where the plan would stand after exactly the steps consumed so far: the soft rule's yardstick.
    // Not "the plan's state before its first remaining step" — a front step that can no longer be
    // used (Refined Touch once its Basic Touch was replaced) would freeze that at the plan's
    // opening numbers, and every step after it, the plan's own included, would read as drift.
    private CraftState _expected;

    private readonly record struct StepChange(int Cp, int Durability, int Progress);

    public BaselineTracker(in CraftParameters parameters, IReadOnlyList<CraftAction> baseline, float deviationPercent)
    {
        _parameters = parameters;
        _deviationPercent = deviationPercent;
        _remaining = new List<CraftAction>(baseline.Count);
        _plannedChange = new List<StepChange>(baseline.Count);

        // The plan's own trajectory, under the all-Normal conditions it was solved for.
        var state = CraftSimulator.Initial(parameters);
        _expected = state;
        foreach (var action in baseline)
        {
            if (CraftSimulator.Use(parameters, state, action, Condition.Normal, true, out var next) != ActionError.None)
                continue; // a step the plan itself could not use is not part of the plan

            _remaining.Add(action);
            _plannedChange.Add(new StepChange(next.Cp - state.Cp, next.Durability - state.Durability, next.Progress - state.Progress));
            state = next;
        }
    }

    public BaselineStatus Status { get; private set; } = BaselineStatus.Alive;

    public bool Alive => Status == BaselineStatus.Alive;

    /// <summary>Steps still to come, in order. Empty once the plan is used up or discarded.</summary>
    public IReadOnlyList<CraftAction> Remaining => Alive ? _remaining : Array.Empty<CraftAction>();

    /// <summary>Snapshot for a search to work on without touching the tracker.</summary>
    public CraftAction[] RemainingSnapshot() => Alive ? _remaining.ToArray() : Array.Empty<CraftAction>();

    /// <summary>The plan's next step, if it has one.</summary>
    public CraftAction? Next => Alive && _remaining.Count > 0 ? _remaining[0] : null;

    /// <summary>Record an action that really happened.</summary>
    public void Observe(CraftAction executed)
    {
        if (!Alive)
            return;

        var index = MatchIndex(_remaining, 0UL, executed);
        if (index < 0)
            return;

        var change = _plannedChange[index];
        _expected.Cp += change.Cp;
        _expected.Durability += change.Durability;
        _expected.Progress += change.Progress;
        _remaining.RemoveAt(index);
        _plannedChange.RemoveAt(index);
    }

    /// <summary>
    /// Decide whether the plan is still worth following from the real state. Once dead it stays
    /// dead — Raphael cannot re-solve mid-craft, so there is nothing to revive it with.
    /// </summary>
    public BaselineStatus Assess(in CraftState actual)
    {
        if (!Alive || _remaining.Count == 0)
            return Status;

        if (AdverseDriftPercent(actual, _expected) > _deviationPercent)
            return Status = BaselineStatus.Drifted;

        if (!CanStillFinish(_parameters, actual, _remaining, 0UL))
            return Status = BaselineStatus.CannotComplete;

        return Status;
    }

    /// <summary>How far BEHIND the plan the craft is, as the worst of CP, durability and progress. Being ahead is not drift.</summary>
    public double AdverseDriftPercent(in CraftState actual, in CraftState planned)
    {
        var cp = (planned.Cp - actual.Cp) / (double)Math.Max(1, _parameters.MaxCp);
        var durability = (planned.Durability - actual.Durability) / (double)Math.Max(1, _parameters.MaxDurability);
        var progress = (planned.Progress - actual.Progress) / (double)Math.Max(1, _parameters.MaxProgress);
        return Math.Max(0, Math.Max(cp, Math.Max(durability, progress))) * 100.0;
    }

    /// <summary>
    /// The hard rule: do the unconsumed steps, played from <paramref name="from"/>, still finish the
    /// craft? The lookahead asks it too, so it never counts on a plan the tracker would drop.
    /// </summary>
    internal static bool CanStillFinish(in CraftParameters parameters, in CraftState from, IReadOnlyList<CraftAction> remaining, ulong consumed)
        => CraftSimulator.IsSuccess(parameters, Replay(parameters, from, remaining, consumed, int.MaxValue, out _));

    /// <summary>
    /// Play the unconsumed steps in order under Normal condition, skipping any that cannot be used
    /// (as a macro skips a line that errors) and any quality action once <paramref name="qualityGoal"/>
    /// is already met.
    /// </summary>
    internal static CraftState Replay(
        in CraftParameters parameters, in CraftState from, IReadOnlyList<CraftAction> remaining, ulong consumed, int qualityGoal, out int stepsUsed)
    {
        var state = from;
        stepsUsed = 0;
        for (var i = 0; i < remaining.Count; i++)
        {
            if (i < 64 && (consumed & (1UL << i)) != 0)
                continue;
            if (CraftSimulator.IsFinal(parameters, state))
                break;

            var action = remaining[i];
            if (state.Quality >= qualityGoal && IsQualityOnly(action))
                continue;
            if (CraftSimulator.Use(parameters, state, action, Condition.Normal, true, out var next) != ActionError.None)
                continue;

            state = next;
            stepsUsed++;
        }

        return state;
    }

    /// <summary>The consume rule on a bit mask, for the lookahead.</summary>
    internal static ulong Consume(CraftAction[] remaining, ulong consumed, CraftAction executed)
    {
        var index = MatchIndex(remaining, consumed, executed);
        return index is >= 0 and < 64 ? consumed | (1UL << index) : consumed;
    }

    /// <summary>Index of the remaining step <paramref name="executed"/> consumes, or -1 for an insertion.</summary>
    private static int MatchIndex(IReadOnlyList<CraftAction> remaining, ulong consumed, CraftAction executed)
    {
        var first = -1;
        var seen = 0;
        for (var i = 0; i < remaining.Count && seen < PullForwardWindow; i++)
        {
            if (i < 64 && (consumed & (1UL << i)) != 0)
                continue;

            if (first < 0)
                first = i;
            if (remaining[i] == executed)
                return i;
            seen++;
        }

        if (first < 0)
            return -1;

        var kind = KindOf(executed);
        return kind != ActionKind.Other && kind == KindOf(remaining[first]) ? first : -1;
    }

    private enum ActionKind
    {
        Other,
        Synthesis,
        Touch,
    }

    private static ActionKind KindOf(CraftAction action) => action switch
    {
        CraftAction.BasicSynthesis or CraftAction.CarefulSynthesis or CraftAction.RapidSynthesis
            or CraftAction.Groundwork or CraftAction.IntensiveSynthesis or CraftAction.PrudentSynthesis
            or CraftAction.MuscleMemory or CraftAction.DelicateSynthesis => ActionKind.Synthesis,
        CraftAction.BasicTouch or CraftAction.StandardTouch or CraftAction.AdvancedTouch
            or CraftAction.PrudentTouch or CraftAction.PreparatoryTouch or CraftAction.PreciseTouch
            or CraftAction.HastyTouch or CraftAction.DaringTouch or CraftAction.RefinedTouch
            or CraftAction.TrainedFinesse or CraftAction.ByregotsBlessing or CraftAction.Reflect
            or CraftAction.TrainedEye => ActionKind.Touch,
        _ => ActionKind.Other,
    };

    /// <summary>Actions whose only purpose is quality — pointless once the goal is met.</summary>
    internal static bool IsQualityOnly(CraftAction action)
        => KindOf(action) == ActionKind.Touch
           || action is CraftAction.Innovation or CraftAction.GreatStrides or CraftAction.QuickInnovation;
}
