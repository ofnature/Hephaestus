using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using Xunit.Abstractions;

namespace Hephaestus.Tests.Features.Crafting.Solver;

/// <summary>
/// Runs only when HEPHAESTUS_BENCHMARK is set, so the default <c>dotnet test</c> stays fast. A
/// plain [Fact(Skip=...)] could not be switched on without editing the source.
/// </summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK")))
            Skip = "Benchmark: set HEPHAESTUS_BENCHMARK=1 to run.";
    }
}

/// <summary>
/// Live versus fixed over the whole <see cref="BenchmarkRecipes"/> set — a report, not a gate
/// (<see cref="LiveVersusFixedTests"/> is the gate). Run i of every recipe uses seed i for BOTH
/// players, so each pair starts from the same dice and the per-seed comparison is meaningful.
///
///   HEPHAESTUS_BENCHMARK=1 dotnet test Hephaestus.Tests -c Release --no-build \
///     --filter Category=Benchmark --logger "console;verbosity=detailed"
///
/// Optional: HEPHAESTUS_BENCHMARK_RUNS (default 200), HEPHAESTUS_BENCHMARK_DEPTH (default 2),
/// HEPHAESTUS_BENCHMARK_ONLY (comma-separated recipe names), HEPHAESTUS_BENCHMARK_OUT (also write
/// the table to this file).
/// </summary>
[Trait("Category", "Benchmark")]
public class LiveVersusFixedBenchmark
{
    private readonly ITestOutputHelper _output;

    public LiveVersusFixedBenchmark(ITestOutputHelper output) => _output = output;

    private readonly record struct Run(bool Completed, int Quality, int Cp);

    [BenchmarkFact]
    public void LiveVersusFixed_AcrossTheRecipeSet()
    {
        var runs = Setting("HEPHAESTUS_BENCHMARK_RUNS", 200);
        var depth = Setting("HEPHAESTUS_BENCHMARK_DEPTH", 2);
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_ONLY")?.Split(',', StringSplitOptions.TrimEntries);
        var settings = new EvaluatorSettings { MaxDepth = depth, Budget = TimeSpan.MaxValue }; // unlimited: deterministic

        var recipes = BenchmarkRecipes.All.Where(r => only is null || only.Contains(r.Name)).ToList();
        var rows = new ConcurrentDictionary<string, string>();
        var clock = Stopwatch.StartNew();

        Parallel.ForEach(recipes, recipe =>
        {
            var fixedRuns = new Run[runs];
            var liveRuns = new Run[runs];
            Parallel.For(0, runs, i =>
            {
                var seed = i + 1;
                var f = RotationPlayer.PlayRolled(recipe.Parameters, recipe.Baseline, recipe.ConditionsFlag, new Random(seed));
                var l = PolicyPlayer.Play(recipe.Parameters, recipe.Baseline, recipe.ConditionsFlag, new Random(seed), settings);
                fixedRuns[i] = new Run(f.Completed, f.Quality, f.Final.Cp);
                liveRuns[i] = new Run(l.Completed, l.Quality, l.Final.Cp);
            });

            rows[recipe.Name] = Row(recipe, fixedRuns, liveRuns);
        });

        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture,
            $"Live (depth {depth}, unlimited budget) vs fixed rotation, {runs} paired runs per recipe (seeds 1..{runs}), {clock.Elapsed.TotalSeconds:F0} s.");
        table.AppendLine("HQ = reached max quality. Worse = runs where live ended below fixed on the same seed (a broken item counts as quality 0).");
        table.AppendLine();
        table.AppendLine("| Recipe | Normal-plan Q | HQ fixed / live | Done fixed / live | Mean Q fixed / live | Worst Q fixed / live | CP left fixed / live | Worse |");
        table.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var recipe in recipes)
            table.AppendLine(rows[recipe.Name]);

        _output.WriteLine(table.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, table.ToString());
    }

    private static string Row(BenchmarkRecipe recipe, Run[] fixedRuns, Run[] liveRuns)
    {
        var max = recipe.Parameters.MaxQuality;
        double Rate(Run[] runs, Func<Run, bool> what) => runs.Count(what) * 100.0 / runs.Length;
        int Scored(Run run) => run.Completed ? run.Quality : 0;
        var worse = fixedRuns.Zip(liveRuns).Count(pair => Scored(pair.Second) < Scored(pair.First)) * 100.0 / fixedRuns.Length;
        var plan = RotationPlayer.PlayNormal(recipe.Parameters, recipe.Baseline).Quality;

        return string.Create(CultureInfo.InvariantCulture,
            $"| {recipe.Name} | {plan}/{max} " +
            $"| {Rate(fixedRuns, r => r.Completed && r.Quality >= max):F0}% / {Rate(liveRuns, r => r.Completed && r.Quality >= max):F0}% " +
            $"| {Rate(fixedRuns, r => r.Completed):F0}% / {Rate(liveRuns, r => r.Completed):F0}% " +
            $"| {fixedRuns.Average(r => r.Quality):F0} / {liveRuns.Average(r => r.Quality):F0} " +
            $"| {fixedRuns.Min(Scored)} / {liveRuns.Min(Scored)} " +
            $"| {fixedRuns.Average(r => r.Cp):F0} / {liveRuns.Average(r => r.Cp):F0} " +
            $"| {worse:F0}% |");
    }

    private static int Setting(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
