using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Tests.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Versus;
using Xunit.Abstractions;

namespace Hephaestus.Tests.Sandbox.Expert;

/// <summary>
/// "What do I need to make this?" - the hard crafts, played the way the plugin plays them (the expert solver with the
/// tuning book's own knobs and the plugin's search), for one crafter with and without what can be eaten, drunk or
/// pressed: no code is judged here, only what the food and the tisane are worth. A report, not a gate.
///
///   HEPHAESTUS_BENCHMARK=1 dotnet test Hephaestus.Tests -c Release --no-build \
///     --filter FullyQualifiedName~WhatDoINeed --logger "console;verbosity=detailed"
///
/// Optional: HEPHAESTUS_SANDBOX_STATS ("craftsmanship,control,cp" with NOTHING eaten; default the user's 5911,5503,641),
/// HEPHAESTUS_BENCHMARK_ONLY (scenario names; default the two the user failed on), HEPHAESTUS_NEED_CRAFTS (crafts per
/// row, default 300), HEPHAESTUS_NEED_MISSIONS (Master missions per row, default 0 = leave the missions out),
/// HEPHAESTUS_NEED_SEARCH (futures per candidate, default 128 = the plugin's; 0 = the policy alone),
/// HEPHAESTUS_NEED_SETS (row names containing any of these, comma separated), HEPHAESTUS_BENCHMARK_OUT.
/// </summary>
[Trait("Category", "Benchmark")]
public class WhatDoINeedBenchmark
{
    private const int ValidationSeed = 500_001;

    /// <summary>What a crafter can add to bare stats. HQ values at their caps, read from the game's sheets (client 2026.11.03).</summary>
    private static readonly (string Name, int Craftsmanship, int Control, int Cp, bool Specialist)[] Sets =
    {
        ("nothing", 0, 0, 0, false),
        ("Cunning Craftsman's Tisane", 0, 0, 27, false),
        ("Ceviche", 150, 0, 96, false),
        ("All i Pebre", 0, 115, 100, false),
        ("All i Pebre + Cunning Tisane", 0, 115, 127, false),
        ("All i Pebre + Commanding Tisane", 0, 178, 100, false),
        ("nothing, specialist actions", 0, 0, 0, true),
        ("All i Pebre, specialist actions", 0, 115, 100, true),
        ("All i Pebre + Cunning Tisane, specialist actions", 0, 115, 127, true),
    };

    private readonly ITestOutputHelper _output;

    public WhatDoINeedBenchmark(ITestOutputHelper output) => _output = output;

    [BenchmarkFact]
    public void WhatDoINeed()
    {
        var crafts = Setting("HEPHAESTUS_NEED_CRAFTS", 300);
        var missionRuns = Setting("HEPHAESTUS_NEED_MISSIONS", 0);
        var samples = Setting("HEPHAESTUS_NEED_SEARCH", 128);
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_ONLY")?.Split(',', StringSplitOptions.TrimEntries)
                   ?? new[] { "AqueductFragment776-Melded", "Gemdraught770-Melded-NqMats" };
        var wanted = Environment.GetEnvironmentVariable("HEPHAESTUS_NEED_SETS")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var bare = (Environment.GetEnvironmentVariable("HEPHAESTUS_SANDBOX_STATS") ?? "5911,5503,641")
            .Split(',', StringSplitOptions.TrimEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var sets = Sets.Where(set => wanted is null || wanted.Any(w => set.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Crafter with nothing eaten: {bare[0]} craftsmanship / {bare[1]} control / {bare[2]} CP. Played as the plugin plays: the tuning book's own knobs, search with {samples} futures. Food and tisane HQ.");
        var clock = Stopwatch.StartNew();

        foreach (var name in only)
        {
            var scenario = VersusScenarios.Named(name);
            var recipe = scenario.Recipe;
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"## {recipe.Item} (rlvl {recipe.RecipeLevel}): progress {recipe.Progress}, quality {recipe.Quality}, durability {recipe.Durability}, starting quality {scenario.InitialQuality}");
            report.AppendLine();
            report.AppendLine("| With | Stats | Reached the demand / the full bar | HQ items per 100 crafts | Quality: lowest tenth | median | Steps | Knobs the book chose |");
            report.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var set in sets)
            {
                var crafter = scenario with { Crafter = set.Name, Craftsmanship = bare[0] + set.Craftsmanship, Control = bare[1] + set.Control, Cp = bare[2] + set.Cp };
                var problem = SandboxProblems.From(crafter, null, set.Specialist);
                var tuning = Book().For(problem, tune: true, threads: Environment.ProcessorCount)();
                var search = samples > 0 ? new SearchSettings(samples, Margin: 0.03, TimeValue: problem.TimeValue) : null;

                var runs = new ExpertRun[crafts];
                Parallel.For(0, crafts, i => runs[i] = ExpertPlayer.Play(problem, tuning, ValidationSeed + i, search));
                var quality = runs.Where(r => r.Completed).Select(r => r.Quality).OrderBy(q => q).ToArray();
                var n = Math.Max(1, quality.Length);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {set.Name} | {crafter.Craftsmanship}/{crafter.Control}/{crafter.Cp} | {runs.Count(r => r.Delivered(problem.Target)) * 100.0 / crafts:F1}% | {(problem.ByHqChance ? runs.Average(r => r.Completed ? Hephaestus.Features.Crafting.Simulation.HqChance.Percent(r.Quality, problem.Parameters.MaxQuality) : 0.0).ToString("F1", CultureInfo.InvariantCulture) : "-")} | {quality.ElementAtOrDefault(n / 10)} | {quality.ElementAtOrDefault(n / 2)} | {runs.Average(r => r.Steps):F1} | {tuning.Changes()} |");
            }
        }

        if (missionRuns > 0)
        {
            foreach (var master in MasterMission.All)
            {
                report.AppendLine();
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"## {master.Name} - {master.Scenario.Recipe.Item}; checkpoints {string.Join(", ", master.Checkpoints.Select(c => c.Score))}");
                report.AppendLine();
                report.AppendLine("| With | Stats | Mean score | Lowest tenth | Checkpoint 1 | Checkpoint 2 | Items | Points per item | Top rating |");
                report.AppendLine("|---|---|---|---|---|---|---|---|---|");
                foreach (var set in sets)
                {
                    var crafter = master.Scenario with { Crafter = set.Name, Craftsmanship = bare[0] + set.Craftsmanship, Control = bare[1] + set.Control, Cp = bare[2] + set.Cp };
                    var mission = master.For(crafter, set.Specialist);
                    var tuning = Book().For(mission.Problem, tune: true, threads: Environment.ProcessorCount)();
                    var search = samples > 0 ? new SearchSettings(samples, Margin: 0.015, TimeValue: mission.Problem.TimeValue) : null;

                    var results = new MissionResult[missionRuns];
                    Parallel.For(0, missionRuns, i => results[i] = MasterMissions.Play(mission, tuning, ValidationSeed + i, search));
                    var sorted = results.Select(r => r.Score).OrderBy(x => x).ToArray();
                    var items = Math.Max(1, results.Sum(r => r.Crafts));
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"| {set.Name} | {crafter.Craftsmanship}/{crafter.Control}/{crafter.Cp} | {results.Average(r => r.Score):F0} | {sorted[missionRuns / 10]} | {results.Count(r => r.Stage >= 1) * 100.0 / missionRuns:F0}% | {results.Count(r => r.Stage >= 2) * 100.0 / missionRuns:F0}% | {results.Average(r => r.Crafts):F1} | {results.Sum(r => r.Points) / (double)items:F0} | {results.Sum(r => r.Gold) * 100.0 / items:F0}% |");
                }
            }
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Whole run: {clock.Elapsed.TotalSeconds:F0} s.");
        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }

    /// <summary>The plugin's tuning book, tuning where it is asked instead of on a background thread.</summary>
    private static ExpertTuningBook Book() => new(work =>
    {
        work();
        return Task.CompletedTask;
    });

    private static int Setting(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;
}
