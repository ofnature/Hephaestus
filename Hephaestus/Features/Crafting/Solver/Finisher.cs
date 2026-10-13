using System.Collections.Generic;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Solver;

/// <summary>
/// A completion-first playout under Normal condition: from any state, push progress to the end
/// with reliable synthesis actions, mending only when the next step would otherwise break the
/// item. It never touches quality.
///
/// Two jobs. It is what the lookahead plays after the baseline's remaining steps run out (or when
/// the baseline is dead), so every leaf answers "can this still be finished, and what is left
/// over?". And that makes it the floor under the whole evaluator: a line is only attractive if
/// the finisher can still close the craft out behind it.
/// </summary>
public static class Finisher
{
    private const int MaxSteps = 60;

    // Reliable progress actions only — the finisher exists to guarantee completion, so nothing that can fail.
    private static readonly CraftAction[] Synthesis =
    {
        CraftAction.Groundwork, CraftAction.CarefulSynthesis, CraftAction.PrudentSynthesis, CraftAction.BasicSynthesis,
    };

    private static readonly CraftAction[] Mends =
    {
        CraftAction.ImmaculateMend, CraftAction.MasterMend, CraftAction.Manipulation,
    };

    /// <param name="record">When given, the actions used are appended (for the Assisted-mode preview).</param>
    public static CraftState Run(in CraftParameters parameters, in CraftState from, out int stepsUsed, List<CraftAction>? record = null)
    {
        var state = from;
        stepsUsed = 0;

        while (stepsUsed < MaxSteps && !CraftSimulator.IsFinal(parameters, state))
        {
            if (!TryPick(parameters, state, out var action))
                break;
            if (CraftSimulator.Use(parameters, state, action, Condition.Normal, true, out var next) != ActionError.None)
                break;

            state = next;
            stepsUsed++;
            record?.Add(action);
        }

        return state;
    }

    private static bool TryPick(in CraftParameters parameters, in CraftState state, out CraftAction action)
    {
        var remaining = parameters.MaxProgress - state.Progress;

        // 1. Anything that finishes right now — the cheapest in CP.
        var found = false;
        var bestCost = int.MaxValue;
        action = default;
        foreach (var candidate in Synthesis)
        {
            if (CraftSimulator.Check(parameters, state, candidate, Condition.Normal) != ActionError.None)
                continue;
            if (CraftSimulator.ProgressGain(parameters, state, candidate, Condition.Normal) < remaining)
                continue;

            var cost = CraftSimulator.CpCost(state, candidate, Condition.Normal);
            if (cost < bestCost)
            {
                (action, bestCost, found) = (candidate, cost, true);
            }
        }

        if (found)
            return true;

        // 2. Otherwise the biggest step that leaves the item in one piece.
        var bestGain = 0;
        foreach (var candidate in Synthesis)
        {
            if (CraftSimulator.Check(parameters, state, candidate, Condition.Normal) != ActionError.None)
                continue;
            if (state.Durability - CraftSimulator.DurabilityCost(state, candidate, Condition.Normal) <= 0)
                continue;

            var gain = CraftSimulator.ProgressGain(parameters, state, candidate, Condition.Normal);
            if (gain > bestGain)
            {
                (action, bestGain, found) = (candidate, gain, true);
            }
        }

        if (found)
        {
            // Veneration pays for itself once there are several synthesis steps still to go.
            if (state.Veneration == 0
                && remaining > bestGain * 2
                && CraftSimulator.Check(parameters, state, CraftAction.Veneration, Condition.Normal) == ActionError.None
                && state.Cp >= 18 + CraftSimulator.CpCost(state, action, Condition.Normal) * 2)
            {
                action = CraftAction.Veneration;
            }

            return true;
        }

        // 3. No step is survivable. With Manipulation running, waiting a turn buys 5 durability.
        if (state.Manipulation > 0
            && CraftSimulator.Check(parameters, state, CraftAction.Observe, Condition.Normal) == ActionError.None)
        {
            action = CraftAction.Observe;
            return true;
        }

        // Otherwise mend if we can, else there is nothing left to try.
        foreach (var mend in Mends)
        {
            if (mend == CraftAction.Manipulation && state.Manipulation > 0)
                continue;
            if (CraftSimulator.Check(parameters, state, mend, Condition.Normal) == ActionError.None)
            {
                action = mend;
                return true;
            }
        }

        return false;
    }
}
