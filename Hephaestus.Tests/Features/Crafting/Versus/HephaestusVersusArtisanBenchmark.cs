using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Artisan.CraftingLogic.Solvers;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Solver;
using Xunit.Abstractions;

namespace Hephaestus.Tests.Features.Crafting.Versus;

/// <summary>One way of crafting a scenario: a name, who it belongs to, and how to seat it at the wheel.</summary>
internal sealed record Contestant(string Name, bool Ours, Func<VersusScenario, IVersusDriver> Seat);

/// <summary>
/// Hephaestus against Artisan, one to one: the same recipe, the same crafter, the same material
/// conditions on every step (<see cref="VersusPlayer"/>), each plugin deciding its own actions.
/// Artisan's side is Artisan's own solver code (External/Artisan), in each of the ways an Artisan
/// user would craft the item; a report, not a gate.
///
///   HEPHAESTUS_BENCHMARK=1 dotnet test Hephaestus.Tests -c Release --no-build \
///     --filter FullyQualifiedName~HephaestusVersusArtisan --logger "console;verbosity=detailed"
///
/// Optional: HEPHAESTUS_BENCHMARK_RUNS (default 200), HEPHAESTUS_BENCHMARK_DEPTH (default 2),
/// HEPHAESTUS_BENCHMARK_ONLY (comma-separated scenario names), HEPHAESTUS_BENCHMARK_OUT (also write
/// the report to this file), HEPHAESTUS_BENCHMARK_TRACES (default 1: per scenario, how many of the
/// crafts Hephaestus lost by the most to print action by action, both sides).
/// HEPHAESTUS_BENCHMARK_PLAYBOOK (default 0 = off; e.g. 16) adds an EXPERIMENT row on expert recipes: live
/// lookahead over Artisan's expert playbook - slow, and not something either plugin ships.
///
/// Not modelled for either side: specialist actions, Cosmic Exploration's Material Miracle and
/// Stellar Steady Hand, food or potions running out, and anything that is not the craft itself.
/// </summary>
[Trait("Category", "Benchmark")]
public class HephaestusVersusArtisanBenchmark
{
    private readonly ITestOutputHelper _output;

    public HephaestusVersusArtisanBenchmark(ITestOutputHelper output) => _output = output;

    /// <summary>Every way the two plugins would make the item. Hephaestus first; it is the yardstick of the last column.</summary>
    internal static IReadOnlyList<Contestant> Contestants(VersusScenario scenario, EvaluatorSettings settings)
    {
        var list = new List<Contestant>
        {
            new("Hephaestus (live)", true,
                s => new HephaestusDriver(s.Parameters, s.Plan, s.Recipe.ConditionsFlag, settings)),
            new("Hephaestus (live, on Artisan's plan)", true,
                s => new HephaestusDriver(s.Parameters, s.BackloadedPlan, s.Recipe.ConditionsFlag, settings)),
        };

        if (scenario.Recipe.Expert)
        {
            list.Add(new("Artisan: Expert Recipe Solver", false,
                s => new ArtisanDriver(ArtisanDriver.Craft(s), new ExpertSolver())));

            // Off in Artisan unless the user turns it on (GenerateOnExperts) - the user has: their Biocoke craft of
            // 2026-11-11 stood at 2,542 progress on step 26, which is this plan to the point.
            list.Add(new("Artisan: Raphael (its opt-in for expert recipes)", false,
                s => Macro(s, s.BackloadedPlan)));

            // Opt-in experiment (HEPHAESTUS_BENCHMARK_PLAYBOOK = futures sampled per candidate): see PlaybookLookaheadDriver.
            if (Setting("HEPHAESTUS_BENCHMARK_PLAYBOOK", 0, allowZero: true) is var samples and > 0)
            {
                list.Add(new($"EXPERIMENT: lookahead over the expert playbook ({samples} futures)", false,
                    s => new PlaybookLookaheadDriver(s, new ArtisanDriver(ArtisanDriver.Craft(s), new ExpertSolver()), samples, seed: 20261111)));
            }
        }
        else
        {
            // Raphael solves are off for expert recipes unless the user turns them on (GenerateOnExperts).
            list.Add(new("Artisan: Raphael", false,
                s => Macro(s, s.BackloadedPlan)));
            if (scenario.ReliablePlan != null)
            {
                list.Add(new("Artisan: Raphael, ensure reliability", false,
                    s => Macro(s, s.ReliablePlan!)));
            }

            list.Add(new("Artisan: Standard Recipe Solver", false,
                s => new ArtisanDriver(ArtisanDriver.Craft(s), new StandardSolver())));
        }

        return list;

        static IVersusDriver Macro(VersusScenario s, CraftAction[] plan)
        {
            var craft = ArtisanDriver.Craft(s);
            return new ArtisanDriver(craft, new MacroSolver(ArtisanDriver.Macro(plan), craft));
        }
    }

    [BenchmarkFact]
    public void HephaestusVersusArtisan_SideBySide()
    {
        var runs = Setting("HEPHAESTUS_BENCHMARK_RUNS", 200);
        var depth = Setting("HEPHAESTUS_BENCHMARK_DEPTH", 2);
        var traces = Setting("HEPHAESTUS_BENCHMARK_TRACES", 1, allowZero: true);
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_ONLY")?.Split(',', StringSplitOptions.TrimEntries);
        var settings = new EvaluatorSettings { MaxDepth = depth, Budget = TimeSpan.MaxValue }; // unlimited: deterministic

        var scenarios = VersusScenarios.All.Where(s => only is null || only.Contains(s.Name)).ToList();
        var sections = new ConcurrentDictionary<string, string>();
        var clock = Stopwatch.StartNew();

        Parallel.ForEach(scenarios, scenario =>
        {
            var contestants = Contestants(scenario, settings);
            var results = new VersusResult[contestants.Count][];
            for (var c = 0; c < contestants.Count; c++)
            {
                var contestant = contestants[c];
                var played = new VersusResult[runs];
                Parallel.For(0, runs, i => played[i] = VersusPlayer.Play(
                    scenario.Parameters, scenario.Recipe.ConditionsFlag, scenario.InitialQuality, seed: i + 1, contestant.Seat(scenario)));
                results[c] = played;
            }

            sections[scenario.Name] = Section(scenario, contestants, results, traces);
        });

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Hephaestus (depth {depth}, unlimited budget) versus Artisan @ c8ee6e0b, {runs} crafts per row on seeds 1..{runs}, {clock.Elapsed.TotalSeconds:F0} s.");
        report.AppendLine("Same seed = the same condition on every step for every row. Delivered = finished, with the quality the recipe demands if it demands one.");
        report.AppendLine("On target = delivered at full quality (a certain HQ), or at the demanded quality. Worst counts an undelivered craft as 0.");
        report.AppendLine("Heph. behind = crafts where Hephaestus (live) ended below this row on the same seed.");
        foreach (var scenario in scenarios)
            report.Append(sections[scenario.Name]);

        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }

    private static string Section(VersusScenario scenario, IReadOnlyList<Contestant> contestants, VersusResult[][] results, int traces)
    {
        var recipe = scenario.Recipe;
        var required = recipe.RequiredQuality;
        var target = scenario.TargetQuality;
        int Scored(VersusResult run) => run.Delivered(required) ? run.Quality : 0;
        double Rate(VersusResult[] runs, Func<VersusResult, bool> what) => runs.Count(what) * 100.0 / runs.Length;

        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"## {scenario.Name}: {recipe.Item} (rlvl {recipe.RecipeLevel}{(recipe.Expert ? ", expert" : "")}), {scenario.Crafter} crafter {scenario.Craftsmanship}/{scenario.Control}/{scenario.Cp}");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Progress {recipe.Progress}, quality {recipe.Quality}{(required > 0 ? $" (fails below {required})" : "")}, durability {recipe.Durability}, starting quality {scenario.InitialQuality}. " +
            $"Raphael on paper: {Math.Min(recipe.Quality, RotationPlayer.PlayNormal(scenario.Parameters, scenario.Plan).Quality + scenario.InitialQuality)} (Hephaestus's plan), " +
            $"{Math.Min(recipe.Quality, RotationPlayer.PlayNormal(scenario.Parameters, scenario.BackloadedPlan).Quality + scenario.InitialQuality)} (Artisan's).");
        text.AppendLine();
        text.AppendLine("| Who | Delivered | On target | Mean quality | Worst | Mean steps | Stalled | Heph. behind |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");

        var ours = results[0];
        for (var c = 0; c < contestants.Count; c++)
        {
            var runs = results[c];
            var behind = c == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
                $"{ours.Zip(runs).Count(pair => Scored(pair.First) < Scored(pair.Second)) * 100.0 / runs.Length:F0}%");
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {contestants[c].Name} | {Rate(runs, r => r.Delivered(required)):F0}% | {Rate(runs, r => r.Delivered(required) && r.Quality >= target):F0}% " +
                $"| {runs.Average(r => r.Quality):F0} | {runs.Min(Scored)} | {runs.Average(r => r.Steps):F1} | {Rate(runs, r => r.Stalled != null):F0}% | {behind} |");
        }

        // How the undelivered crafts ended. "Finished short" with CP or durability to spare is a craft
        // that was closed out while it could still have been worked on; with nothing left, it was out of means.
        for (var c = 0; c < contestants.Count; c++)
        {
            var lost = results[c].Where(r => !r.Delivered(required)).ToList();
            if (lost.Count == 0)
                continue;

            var broke = lost.Count(r => !r.Completed && r.Stalled == null);
            var shortOnes = lost.Where(r => r.Completed).ToList();
            var line = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"- {contestants[c].Name}: {lost.Count} undelivered — {broke} broke the item"));
            if (shortOnes.Count > 0)
            {
                line.Append(CultureInfo.InvariantCulture,
                    $", {shortOnes.Count} finished below the demand (on average {required - shortOnes.Average(r => r.Quality):F0} quality short, with {shortOnes.Average(r => r.Final.Cp):F0} CP and {shortOnes.Average(r => r.Final.Durability):F0} durability unspent)");
            }

            text.AppendLine(line.ToString());
        }

        // Why a row stalled, when one did: a stall is a craft somebody would have to finish by hand.
        for (var c = 0; c < contestants.Count; c++)
        {
            foreach (var reason in results[c].Where(r => r.Stalled != null).GroupBy(r => r.Stalled!).OrderByDescending(g => g.Count()).Take(3))
                text.AppendLine(CultureInfo.InvariantCulture, $"- {contestants[c].Name} stalled {reason.Count()}x: {reason.Key} (first on seed {Array.IndexOf(results[c], reason.First()) + 1})");
        }

        // The crafts Hephaestus lost by the most to any Artisan row, action by action.
        var losses = new List<(int Margin, int Seed, int Rival)>();
        for (var c = 0; c < contestants.Count; c++)
        {
            if (contestants[c].Ours)
                continue;
            for (var i = 0; i < ours.Length; i++)
            {
                var margin = Scored(results[c][i]) - Scored(ours[i]);
                if (margin > 0)
                    losses.Add((margin, i + 1, c));
            }
        }

        foreach (var (margin, seed, rival) in losses.OrderByDescending(l => l.Margin).DistinctBy(l => l.Seed).Take(traces))
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Seed {seed}: Hephaestus {Describe(ours[seed - 1], required)} vs {contestants[rival].Name} {Describe(results[rival][seed - 1], required)} ({margin} behind).");
            text.AppendLine(CultureInfo.InvariantCulture, $"  Hephaestus: {Trace(ours[seed - 1])}");
            text.AppendLine(CultureInfo.InvariantCulture, $"  Artisan:    {Trace(results[rival][seed - 1])}");
        }

        return text.ToString();
    }

    private static string Describe(VersusResult run, int required)
        => run.Delivered(required) ? $"quality {run.Quality}" : run.Completed ? $"FAILED the quality demand at {run.Quality}" : run.Stalled != null ? $"STALLED ({run.Stalled})" : "BROKE the item";

    /// <summary>The actions in order; a condition other than Normal is written before the action taken under it, a failed roll after it.</summary>
    internal static string Trace(VersusResult run)
        => string.Join(" > ", run.Trace.Select(step =>
            (step.Condition == Condition.Normal ? "" : $"[{step.Condition}] ") + step.Action + (step.Success ? "" : " (failed)")));

    private static int Setting(string name, int fallback, bool allowZero = false)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && (value > 0 || (allowZero && value == 0)) ? value : fallback;
}
