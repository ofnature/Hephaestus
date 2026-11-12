using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Features.Crafting.Planner;

/// <summary>
/// The planner's second stage: take a finished rotation and make it better by small
/// edits - drop an action, put one in, change one for another, swap two neighbours, move one elsewhere - each judged
/// by playing the whole rotation through the simulator. An edit that helps is kept; one that hurts is kept now and
/// then (less and less as the run goes on), which is what lets the search leave a rotation that no single edit
/// improves. Simulated annealing, in other words, over rotations instead of over states.
///
/// It finds what the beam's ranking cannot see coming: that a Great Strides is worth its step only before a
/// Preparatory Touch, that Waste Not II should lie over exactly the eight dearest steps. A rotation is judged, in
/// this order, by: finishing the item; reaching the target quality; fewer steps; more quality.
/// </summary>
public static class PlanPolisher
{
    private static readonly CraftAction[] Actions =
    {
        MuscleMemory, Reflect,
        Veneration, Innovation, GreatStrides, WasteNot, WasteNot2, Manipulation, TrainedPerfection,
        Groundwork, CarefulSynthesis, PrudentSynthesis, BasicSynthesis, DelicateSynthesis,
        PreparatoryTouch, BasicTouch, StandardTouch, AdvancedTouch, PrudentTouch, RefinedTouch, TrainedFinesse, ByregotsBlessing,
        Observe, ImmaculateMend, MasterMend,
        HeartAndSoul, QuickInnovation, IntensiveSynthesis, PreciseTouch,
    };

    /// <summary>
    /// Combinations that only pay as a whole, offered to the search as single edits: no chain of one-action edits
    /// gets from "nothing" to "Great Strides, then the big touch" without passing through something worse.
    /// </summary>
    private static readonly CraftAction[][] Chains =
    {
        new[] { GreatStrides, PreparatoryTouch },
        new[] { Innovation, PreparatoryTouch, PreparatoryTouch },
        new[] { GreatStrides, Innovation, ByregotsBlessing },
        new[] { GreatStrides, ByregotsBlessing },
        new[] { BasicTouch, StandardTouch, AdvancedTouch },
        new[] { BasicTouch, RefinedTouch },
        new[] { Observe, AdvancedTouch },
        new[] { Veneration, Groundwork, Groundwork },
        new[] { WasteNot2, Groundwork, Groundwork },
        new[] { WasteNot, PreparatoryTouch, PreparatoryTouch },
        new[] { TrainedPerfection, PreparatoryTouch },
        new[] { TrainedPerfection, Groundwork },
        new[] { DelicateSynthesis, DelicateSynthesis },
        new[] { HeartAndSoul, PreciseTouch },
        new[] { HeartAndSoul, IntensiveSynthesis },
        new[] { QuickInnovation, ByregotsBlessing },
        new[] { GreatStrides, QuickInnovation, ByregotsBlessing },
    };

    /// <summary>
    /// Rotations of different SHAPES to start from besides the beam's own: where the progress goes (first, last, mixed
    /// in with Delicate Synthesis) is a choice no run of small edits undoes. Textbook shapes, not anyone's solution -
    /// what in them cannot be used on this recipe is simply skipped, and the search cuts and fills the rest.
    /// </summary>
    internal static readonly CraftAction[][] Shapes =
    {
        new[]
        {
            MuscleMemory, Veneration, WasteNot2, Groundwork, Groundwork, Groundwork, Groundwork, Innovation, PreparatoryTouch, PreparatoryTouch,
            PreparatoryTouch, GreatStrides, Innovation, ByregotsBlessing, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis,
        },
        new[]
        {
            Reflect, WasteNot2, Innovation, PreparatoryTouch, PreparatoryTouch, PreparatoryTouch, PreparatoryTouch, GreatStrides, Innovation,
            ByregotsBlessing, Veneration, Groundwork, Groundwork, Groundwork, Groundwork, Groundwork,
        },
        new[]
        {
            MuscleMemory, Manipulation, Veneration, WasteNot2, Groundwork, DelicateSynthesis, DelicateSynthesis, DelicateSynthesis, Innovation,
            PreparatoryTouch, PreparatoryTouch, GreatStrides, PreparatoryTouch, GreatStrides, Innovation, ByregotsBlessing, CarefulSynthesis, CarefulSynthesis,
        },
        new[]
        {
            Reflect, BasicTouch, RefinedTouch, Innovation, PreparatoryTouch, BasicTouch, StandardTouch, AdvancedTouch, ImmaculateMend, GreatStrides,
            Innovation, ByregotsBlessing, Veneration, WasteNot2, Groundwork, Groundwork, Groundwork, Groundwork, Groundwork,
        },
    };

    /// <param name="iterations">Edits tried per run.</param>
    /// <param name="restarts">Runs per starting rotation (the given one and each of the <see cref="Shapes"/>), all in parallel.</param>
    /// <param name="shapes">False = start from the given rotation only.</param>
    public static RotationPlan Polish(
        CraftParameters parameters, int initialQuality, int target, CraftAction[] seed, int iterations = 200_000, int restarts = 4, int rngSeed = 1,
        bool shapes = true, int threads = 0)
    {
        target = Math.Min(target, parameters.MaxQuality);
        var seeds = new List<CraftAction[]> { seed };
        if (shapes)
            seeds.AddRange(Shapes);

        // First pass: every start, several times over - which run finds the short rotation is luck, so there are many.
        var found = Run(parameters, initialQuality, target, seeds, restarts, iterations, heat: 3.0, rngSeed, threads);

        // Second pass: the best few rotations of the first, warmed up again a little. A rotation a run ENDED near is
        // often one or two edits from a better one that its cooling no longer allowed.
        var elite = found.Where(plan => plan.Completed).Distinct(PlanComparer.Instance).OrderBy(plan => plan, PlanComparer.Instance).Take(3).ToList();
        if (elite.Count > 0)
            found.AddRange(Run(parameters, initialQuality, target, elite.Select(plan => plan.Actions).ToList(), restarts, iterations / 2, heat: 1.5, rngSeed + 1, threads));

        return found.OrderBy(plan => plan, PlanComparer.Instance).First();
    }

    private static List<RotationPlan> Run(
        CraftParameters parameters, int initialQuality, int target, List<CraftAction[]> seeds, int restarts, int iterations, double heat, int rngSeed,
        int threads)
    {
        // Inside the game's process (threads > 0) the runs take a share of the cores and stand back for the game's own threads.
        var results = new CraftAction[seeds.Count * restarts][];
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : -1 };
        Parallel.For(0, results.Length, options, r =>
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            if (threads > 0)
                thread.Priority = ThreadPriority.BelowNormal;
            try
            {
                results[r] = Anneal(parameters, initialQuality, target, seeds[r / restarts], iterations, heat, strict: r % 2 == 1, new Random(rngSeed * 7919 + r));
            }
            finally
            {
                thread.Priority = priority;
            }
        });

        var plans = new List<RotationPlan>(results.Length);
        foreach (var plan in results)
        {
            var end = Play(parameters, initialQuality, plan, out _, out var completed);
            plans.Add(new RotationPlan(plan, completed, completed && end.Quality >= target, end.Quality));
        }

        return plans;
    }

    /// <summary>Best first by <see cref="Better"/>; equal = the same actions in the same order.</summary>
    private sealed class PlanComparer : IComparer<RotationPlan>, IEqualityComparer<RotationPlan>
    {
        public static readonly PlanComparer Instance = new();

        public int Compare(RotationPlan? x, RotationPlan? y) => ReferenceEquals(x, y) ? 0 : Better(x!, y!) ? -1 : Better(y!, x!) ? 1 : 0;

        public bool Equals(RotationPlan? x, RotationPlan? y) => x!.Actions.AsSpan().SequenceEqual(y!.Actions);

        public int GetHashCode(RotationPlan plan)
        {
            var hash = new HashCode();
            foreach (var action in plan.Actions)
                hash.Add(action);
            return hash.ToHashCode();
        }
    }

    /// <summary>Finishing beats not finishing; reaching the target beats falling short; then fewer steps; short of the target, more quality.</summary>
    private static bool Better(RotationPlan plan, RotationPlan than)
    {
        if (plan.Completed != than.Completed)
            return plan.Completed;
        if (plan.Reached != than.Reached)
            return plan.Reached;
        if (plan.Reached)
            return plan.Steps < than.Steps || (plan.Steps == than.Steps && plan.Quality > than.Quality);
        return plan.Quality > than.Quality || (plan.Quality == than.Quality && plan.Steps < than.Steps);
    }

    /// <summary>One run. What it returns is the best rotation it PASSED THROUGH by <see cref="Better"/>, not where it ended.</summary>
    private static CraftAction[] Anneal(
        in CraftParameters parameters, int initialQuality, int target, CraftAction[] seed, int iterations, double startHeat, bool strict, Random random)
    {
        var buffer = new List<CraftAction>(64);
        var current = new List<CraftAction>(seed);
        var now = Judge(parameters, initialQuality, target, current, buffer);
        current = new List<CraftAction>(buffer); // only what was really used: an edit never has to fight dead weight
        var best = new RotationPlan(buffer.ToArray(), now.Completed, now.Completed && now.Quality >= target, now.Quality);
        var candidate = new List<CraftAction>(64);

        for (var i = 0; i < iterations; i++)
        {
            // A step is worth 1. The heat falls from "a few steps worse is often fine" to "nothing worse is"; the price
            // of a percent of the target missed RISES, from three steps to fifteen. Early on the search may walk through
            // rotations that fall short - that is how it gets from one way of making the item to another - and by the end
            // it is being pushed back over the line: only a rotation that reaches the target can be the answer.
            // Every other run is STRICT instead - fifteen from the first edit: where the target is only just in reach,
            // a run that wanders off it early does not find its way back, and the shortening has to happen on the line.
            var done = i / (double)iterations;
            var heat = startHeat * (1.0 - done) + 0.01;
            var shortfall = strict ? 15.0 : 3.0 + 12.0 * done;

            candidate.Clear();
            candidate.AddRange(current);
            Edit(candidate, random);
            var then = Judge(parameters, initialQuality, target, candidate, buffer);
            var gain = then.Score(target, shortfall) - now.Score(target, shortfall);
            if (gain < 0 && random.NextDouble() >= Math.Exp(gain / heat))
                continue;

            current.Clear();
            current.AddRange(buffer);
            now = then;

            var reached = then.Completed && then.Quality >= target;
            if (then.Completed && (reached != best.Reached ? reached
                    : reached ? buffer.Count < best.Steps || (buffer.Count == best.Steps && then.Quality > best.Quality)
                    : !best.Completed || then.Quality > best.Quality || (then.Quality == best.Quality && buffer.Count < best.Steps)))
            {
                best = new RotationPlan(buffer.ToArray(), true, reached, then.Quality);
            }
        }

        return best.Actions;
    }

    /// <summary>Where a rotation ends, played under Normal conditions.</summary>
    private readonly record struct Outcome(bool Completed, bool Broke, int Quality, int Progress, int MaxProgress, int Steps, int Cp)
    {
        /// <summary>On one smooth scale, so that the search can trade: a step costs 1, a percent of the target missed costs <paramref name="shortfall"/>.</summary>
        public double Score(int target, double shortfall)
        {
            var reach = shortfall * 100.0 * Math.Min(Quality, target) / Math.Max(1, target);
            if (Completed)
                return reach - Steps + 0.001 * Cp;

            // Not finished: a missing percent of progress costs more than a missing percent of quality ever does; a broken item most of all.
            return reach - Steps - 2000.0 * (1.0 - Progress / (double)MaxProgress) - (Broke ? 400 : 100);
        }
    }

    private static void Edit(List<CraftAction> plan, Random random)
    {
        var n = plan.Count;
        switch (n == 0 ? 1 : random.Next(8))
        {
            case 6: // put a whole chain in
                plan.InsertRange(random.Next(n + 1), Chains[random.Next(Chains.Length)]);
                break;
            case 7 when n >= 3: // a chain in place of a stretch of the same length
            {
                var chain = Chains[random.Next(Chains.Length)];
                var at = random.Next(n - Math.Min(n, chain.Length) + 1);
                plan.RemoveRange(at, Math.Min(chain.Length, n - at));
                plan.InsertRange(at, chain);
                break;
            }

            case 0: // drop one
                plan.RemoveAt(random.Next(n));
                break;
            case 1: // put one in
                plan.Insert(random.Next(n + 1), Actions[random.Next(Actions.Length)]);
                break;
            case 2: // change one
                plan[random.Next(n)] = Actions[random.Next(Actions.Length)];
                break;
            case 3 when n >= 2: // swap two neighbours
            {
                var i = random.Next(n - 1);
                (plan[i], plan[i + 1]) = (plan[i + 1], plan[i]);
                break;
            }

            case 4 when n >= 2: // move one elsewhere
            {
                var from = random.Next(n);
                var action = plan[from];
                plan.RemoveAt(from);
                plan.Insert(random.Next(plan.Count + 1), action);
                break;
            }

            default: // change one and drop another: a trade no single edit can make
                plan[random.Next(n)] = Actions[random.Next(Actions.Length)];
                if (n >= 2)
                    plan.RemoveAt(random.Next(n));
                break;
        }
    }

    /// <summary>
    /// Plays a rotation under Normal conditions. An action that cannot be used when its turn comes is skipped (as a
    /// macro line would be) and left out of <paramref name="used"/> - what comes back is the rotation that was really played.
    /// </summary>
    private static Outcome Judge(in CraftParameters parameters, int initialQuality, int target, List<CraftAction> plan, List<CraftAction> used)
    {
        used.Clear();
        var state = CraftSimulator.Initial(parameters);
        state.Quality = Math.Min(parameters.MaxQuality, initialQuality);
        foreach (var action in plan)
        {
            if (CraftSimulator.Use(parameters, state, action, Condition.Normal, success: true, out var next) != ActionError.None)
                continue;

            state = next;
            used.Add(action);
            if (CraftSimulator.IsFinal(parameters, state))
                break;
        }

        var completed = CraftSimulator.IsSuccess(parameters, state);
        return new Outcome(completed, !completed && CraftSimulator.IsFinal(parameters, state), state.Quality, state.Progress, parameters.MaxProgress, used.Count, state.Cp);
    }

    private static CraftState Play(in CraftParameters parameters, int initialQuality, CraftAction[] plan, out int used, out bool completed)
    {
        used = 0;
        var state = CraftSimulator.Initial(parameters);
        state.Quality = Math.Min(parameters.MaxQuality, initialQuality);
        foreach (var action in plan)
        {
            if (CraftSimulator.Use(parameters, state, action, Condition.Normal, success: true, out var next) != ActionError.None)
                continue;
            state = next;
            used++;
            if (CraftSimulator.IsFinal(parameters, state))
                break;
        }

        completed = CraftSimulator.IsSuccess(parameters, state);
        return state;
    }
}
