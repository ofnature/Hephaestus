using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Features.Crafting.Planner;

/// <param name="Beam">States kept per step.</param>
/// <param name="MaxSteps">A plan longer than this is not looked for.</param>
/// <param name="TouchRate">Quality a step of quality work is taken to make, in base touches at the Inner Quiet in hand (buff steps averaged in).</param>
/// <param name="TouchCp">CP a step of quality work is taken to cost.</param>
public sealed record PlannerSettings(int Beam = 3000, int MaxSteps = 45, double TouchRate = 2.0, double TouchCp = 30);

/// <param name="Reached">The plan finishes the item at the target quality (under Normal conditions throughout).</param>
/// <param name="Quality">Quality at the end, starting quality included.</param>
public sealed record RotationPlan(CraftAction[] Actions, bool Completed, bool Reached, int Quality)
{
    public int Steps => Actions.Length;
}

/// <summary>
/// OUR OWN rotation planner, standing where raphael-cli stood: the shortest
/// rotation it can find that finishes the item at the target quality, under Normal conditions throughout and with
/// nothing left to chance (no Rapid Synthesis, no Hasty Touch, nothing that needs a Good - unless the crafter is a
/// specialist with Heart and Soul allowed, which opens one such action). When the target cannot be reached on paper,
/// the finished rotation with the most quality.
///
/// A beam search over the plugin's own simulator: step by step, every state kept is tried with every action, and of
/// what comes out the best <see cref="PlannerSettings.Beam"/> are kept - "best" being how far along the item is
/// (progress, quality), what it still has to spend (CP, durability) and what its buffs are still worth. The first
/// step count at which a state finishes at the target is the answer: no shorter one was found. It is not PROVEN
/// shortest, as Raphael's is; how close it comes is what the tests' PlannerBenchmark measures (level on most
/// recipes, a step or two behind on some).
///
/// The plan is a reference, not a rail: the live evaluator follows it and leaves it as the conditions roll.
/// </summary>
public static class RotationPlanner
{
    private static readonly CraftAction[] Actions =
    {
        MuscleMemory, Reflect, TrainedEye,
        Veneration, Innovation, GreatStrides, WasteNot, WasteNot2, Manipulation, TrainedPerfection,
        Groundwork, CarefulSynthesis, PrudentSynthesis, BasicSynthesis, DelicateSynthesis,
        PreparatoryTouch, BasicTouch, StandardTouch, AdvancedTouch, PrudentTouch, RefinedTouch, TrainedFinesse, ByregotsBlessing,
        Observe, ImmaculateMend, MasterMend,

        // A specialist's, where the craft allows them: Heart and Soul opens the three actions a Good step would.
        HeartAndSoul, QuickInnovation, IntensiveSynthesis, PreciseTouch, TricksOfTheTrade,
    };

    private struct Node
    {
        public CraftState State;
        public int Parent;
        public CraftAction Action;
        public double Score;
    }

    public static RotationPlan Solve(in CraftParameters parameters, int initialQuality, int target, PlannerSettings? settings = null)
    {
        settings ??= new PlannerSettings();
        target = Math.Min(target, parameters.MaxQuality);

        var start = CraftSimulator.Initial(parameters);
        start.Quality = Math.Min(parameters.MaxQuality, initialQuality);
        var layers = new List<Node[]> { new[] { new Node { State = start, Parent = -1 } } };

        (int Layer, int Parent, CraftAction Action, int Quality)? shortfall = null; // finished, but short of the target
        var seen = new HashSet<(ulong, ulong)>();
        var next = new List<Node>();

        for (var depth = 0; depth < settings.MaxSteps; depth++)
        {
            var layer = layers[depth];
            next.Clear();
            seen.Clear();
            (int Parent, CraftAction Action, CraftState State)? reached = null;

            for (var i = 0; i < layer.Length; i++)
            {
                var state = layer[i].State;
                foreach (var action in Actions)
                {
                    if (CraftSimulator.Use(parameters, state, action, Condition.Normal, success: true, out var after) != ActionError.None)
                        continue;

                    if (CraftSimulator.IsFinal(parameters, after))
                    {
                        if (!CraftSimulator.IsSuccess(parameters, after))
                            continue; // the item broke

                        if (after.Quality >= target)
                        {
                            // Among the finishes of this step count: the most quality, then the most CP unspent.
                            if (reached is not { } best || after.Quality > best.State.Quality
                                || (after.Quality == best.State.Quality && after.Cp > best.State.Cp))
                            {
                                reached = (i, action, after);
                            }
                        }
                        else if (shortfall is not { } most || after.Quality > most.Quality)
                        {
                            shortfall = (depth, i, action, after.Quality);
                        }

                        continue;
                    }

                    if (seen.Add(Key(after)))
                        next.Add(new Node { State = after, Parent = i, Action = action, Score = Score(parameters, target, settings, after) });
                }
            }

            if (reached is { } done)
                return new RotationPlan(Trace(layers, depth, done.Parent, done.Action), true, true, done.State.Quality);

            if (next.Count == 0)
                break;

            if (next.Count > settings.Beam)
            {
                // Most of the beam goes to the states that look closest to done at the target. An eighth is kept for the
                // states furthest along in PROGRESS, whatever their quality: when the target turns out to be out of
                // reach, those are the ones that can still finish the item, and a plan that does not finish is no plan.
                var finishers = Math.Max(1, settings.Beam / 8);
                next.Sort(static (a, b) => b.Score.CompareTo(a.Score));
                var rest = next.GetRange(settings.Beam - finishers, next.Count - (settings.Beam - finishers));
                next.RemoveRange(settings.Beam - finishers, next.Count - (settings.Beam - finishers));
                rest.Sort(static (a, b) => a.State.Progress != b.State.Progress
                    ? b.State.Progress.CompareTo(a.State.Progress)
                    : b.State.Quality.CompareTo(a.State.Quality));
                next.AddRange(rest.GetRange(0, Math.Min(finishers, rest.Count)));
            }

            layers.Add(next.ToArray());
        }

        return shortfall is { } least
            ? new RotationPlan(Trace(layers, least.Layer, least.Parent, least.Action), true, false, least.Quality)
            : new RotationPlan(Array.Empty<CraftAction>(), false, false, start.Quality);
    }

    /// <summary>
    /// The best plan over a handful of ways of weighing a half-made item against each other. No one weighing is
    /// right for every recipe (how dear CP is depends on how much the recipe leaves over), and a search costs little.
    /// </summary>
    public static RotationPlan SolveBest(in CraftParameters parameters, int initialQuality, int target, int beam = 3000)
    {
        RotationPlan? best = null;
        foreach (var rate in new[] { 1.6, 2.0, 2.5 })
        foreach (var cp in new[] { 24.0, 34.0 }) // a heavier style (3.0 at 44 CP) was tried: it changed nothing
        {
            var plan = Solve(parameters, initialQuality, target, new PlannerSettings(beam, TouchRate: rate, TouchCp: cp));
            if (best is null || Better(plan, best))
                best = plan;
        }

        return best!;
    }

    /// <summary>
    /// The whole planner: the beam finds a rotation that finishes, <see cref="PlanPolisher"/> makes it shorter (or, short
    /// of the target, richer). The polished rotation is only taken if it is in fact better.
    /// </summary>
    /// <param name="iterations">Edits the polisher tries per run; 0 = the beam's rotation as it is.</param>
    /// <param name="threads">Most threads the polisher may use, at below-normal priority; 0 = all of them, as they come (tests, benches).</param>
    public static RotationPlan Plan(
        in CraftParameters parameters, int initialQuality, int target, int beam = 3000, int iterations = 1_000_000, int threads = 0)
    {
        var rough = SolveBest(parameters, initialQuality, target, beam);
        if (!rough.Completed || iterations <= 0)
            return rough;

        var polished = PlanPolisher.Polish(parameters, initialQuality, target, rough.Actions, iterations, threads: threads);
        return Better(polished, rough) ? polished : rough;
    }

    /// <summary>Reaching the target beats not reaching it; then fewer steps; short of the target, more quality.</summary>
    public static bool Better(RotationPlan plan, RotationPlan than)
    {
        if (plan.Reached != than.Reached)
            return plan.Reached;
        if (plan.Reached)
            return plan.Steps < than.Steps || (plan.Steps == than.Steps && plan.Quality > than.Quality);
        if (plan.Completed != than.Completed)
            return plan.Completed;
        return plan.Quality > than.Quality || (plan.Quality == than.Quality && plan.Steps < than.Steps);
    }

    /// <summary>
    /// For ranking states that took the same number of steps: MINUS the steps the item still seems to need - quality
    /// work at the Inner Quiet in hand, the closing Byregot's Blessing, progress in Groundwork under Veneration - with
    /// every CP and point of durability that work would lack counted as steps it cannot take, and standing buffs
    /// counted as steps already paid for. A guess, deliberately on the hopeful side; the beam does the rest.
    /// </summary>
    private static double Score(in CraftParameters parameters, int target, PlannerSettings settings, in CraftState state)
    {
        var progressLeft = Math.Max(0, parameters.MaxProgress - state.Progress);
        var qualityLeft = Math.Max(0, target - state.Quality);
        var stacks = 1.0 + state.InnerQuiet / 10.0;

        // Quality: the blessing takes a slice in one go (two buffs and itself), touches the rest.
        var steps = 0.0;
        var cp = 0.0;
        var wear = 0.0;
        if (qualityLeft > 0)
        {
            var blessing = state.InnerQuiet > 0 ? parameters.BaseQuality * stacks * (1.0 + 0.2 * state.InnerQuiet) * 2.5 : 0;
            if (blessing > 0)
            {
                steps += qualityLeft > blessing * 0.4 ? 3 : 1;
                cp += qualityLeft > blessing * 0.4 ? 24 + 32 + 18 : 24;
                wear += 10;
            }

            var byTouch = Math.Max(0, qualityLeft - blessing);
            var touches = byTouch / (settings.TouchRate * parameters.BaseQuality * stacks);
            steps += touches;
            cp += touches * settings.TouchCp;
            wear += touches * 10;
        }

        // Progress: Groundwork under Veneration, a Veneration every four of them.
        var synths = progressLeft / (5.4 * Math.Max(1, parameters.BaseProgress));
        steps += synths * 1.25;
        cp += synths * (18 + 18 / 4.0);
        wear += synths * 15;

        // What is in hand, and what standing buffs have already paid for.
        var durability = state.Durability - 5 + 5.0 * state.Manipulation + 5.0 * state.WasteNot
                         + (state.TrainedPerfectionAvailable || state.TrainedPerfectionActive ? 20 : 0);
        var worn = Math.Max(0, wear - durability);
        cp += worn * 1.7;         // Immaculate Mend's price per point
        steps += worn / 60.0;     // ...and its step

        steps -= 0.3 * Math.Min((int)state.Innovation, 4) * (qualityLeft > 0 ? 1 : 0);
        steps -= state.GreatStrides > 0 && qualityLeft > 0 ? 0.8 : 0;
        steps -= 0.3 * Math.Min((int)state.Veneration, 4) * (progressLeft > 0 ? 1 : 0);
        steps -= state.MuscleMemory > 0 && progressLeft > 0 ? 0.6 : 0;

        // CP the item does not have is work that cannot be done at all: far dearer than a step.
        var missing = Math.Max(0, cp - state.Cp);
        return -(steps + missing / 6.0) + 0.002 * (state.Cp - cp);
    }

    private static CraftAction[] Trace(List<Node[]> layers, int layer, int parent, CraftAction last)
    {
        var actions = new List<CraftAction> { last };
        for (var depth = layer; depth > 0; depth--)
        {
            var node = layers[depth][parent];
            actions.Add(node.Action);
            parent = node.Parent;
        }

        actions.Reverse();
        return actions.ToArray();
    }

    /// <summary>Everything about a state that the rest of the craft can depend on, packed: two states with one key are one state.</summary>
    private static (ulong, ulong) Key(in CraftState s)
    {
        var a = (ulong)(uint)s.Cp | ((ulong)(uint)s.Durability << 12) | ((ulong)(uint)s.Progress << 20) | ((ulong)(uint)s.Quality << 40);
        var b = s.InnerQuiet | ((ulong)s.WasteNot << 4) | ((ulong)s.Innovation << 8) | ((ulong)s.Veneration << 12)
                | ((ulong)s.GreatStrides << 16) | ((ulong)s.MuscleMemory << 20) | ((ulong)s.Manipulation << 24)
                | ((ulong)s.Combo << 28) | (s.TrainedPerfectionAvailable ? 1UL << 32 : 0) | (s.TrainedPerfectionActive ? 1UL << 33 : 0)
                | (s.Expedience ? 1UL << 34 : 0) | (s.HeartAndSoulAvailable ? 1UL << 35 : 0) | (s.HeartAndSoulActive ? 1UL << 36 : 0)
                | (s.QuickInnovationAvailable ? 1UL << 37 : 0);
        return (a, b);
    }
}
