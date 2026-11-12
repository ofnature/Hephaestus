using Hephaestus.Features.Crafting.Expert;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hephaestus.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Versus;
using Xunit.Abstractions;

namespace Hephaestus.Tests.Sandbox.Expert;

/// <summary>
/// The sandbox's bench for Cosmic Exploration's "Master" missions - the hardest crafting there is, and what the
/// expert solver is polished for: whole missions, scored the way the mission scores (points per item by its
/// rating, the multipliers, the clock). Tunes <see cref="ExpertPolicy"/> per mission ON THE MISSION SCORE, then
/// measures on missions nobody was tuned on, beside everything else that could make the items. A report, not a gate.
///
///   HEPHAESTUS_BENCHMARK=1 dotnet test Hephaestus.Tests -c Release --no-build \
///     --filter FullyQualifiedName~MasterMissionBenchmark --logger "console;verbosity=detailed"
///
/// Optional: HEPHAESTUS_MISSION_TUNE (missions per setting tried, default 400; 0 = no tuning),
/// HEPHAESTUS_MISSION_RUNS (missions per row, default 400), HEPHAESTUS_MISSION_SEARCH (futures per candidate,
/// default 0 = no search rows), HEPHAESTUS_MISSION_SEARCH_RUNS (missions per search row, default 60),
/// HEPHAESTUS_MISSION_OTHERS (missions per row for the plugin's solver and Artisan, default 100; 0 = leave them out),
/// HEPHAESTUS_MISSION_ONLY ("1,3"), HEPHAESTUS_MISSION_OVERHEAD (seconds between items, default 6),
/// HEPHAESTUS_MISSION_PACE (percent of the simulator's clock, default 100), HEPHAESTUS_MISSION_KNOBS
/// ("Knob=value,..." laid over the defaults before tuning), HEPHAESTUS_SANDBOX_STATS ("craftsmanship,control,cp"),
/// HEPHAESTUS_BENCHMARK_OUT (also write the report here).
/// </summary>
[Trait("Category", "Benchmark")]
public class MasterMissionBenchmark
{
    private const int ValidationSeed = 100_001;

    private readonly ITestOutputHelper _output;

    public MasterMissionBenchmark(ITestOutputHelper output) => _output = output;

    [BenchmarkFact]
    public void MasterMissions_TuneThenMeasure()
    {
        var tuneRuns = Setting("HEPHAESTUS_MISSION_TUNE", 400);
        var runs = Setting("HEPHAESTUS_MISSION_RUNS", 400);
        var samples = Setting("HEPHAESTUS_MISSION_SEARCH", 0);
        var searchRuns = Setting("HEPHAESTUS_MISSION_SEARCH_RUNS", 60);
        var otherRuns = Setting("HEPHAESTUS_MISSION_OTHERS", 100);
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_MISSION_ONLY")?.Split(',', StringSplitOptions.TrimEntries);
        MasterMissions.OverheadSeconds = Setting("HEPHAESTUS_MISSION_OVERHEAD", 6);
        MasterMissions.Pace = Setting("HEPHAESTUS_MISSION_PACE", 100) / 100.0;
        var start = ExpertSandboxBenchmark.Knobs(Environment.GetEnvironmentVariable("HEPHAESTUS_MISSION_KNOBS"));

        var report = new StringBuilder();
        var missions = MasterMission.All.Where((_, i) => only is null || only.Contains((i + 1).ToString(CultureInfo.InvariantCulture))).ToList();
        var ownStats = true;
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_SANDBOX_STATS") is { Length: > 0 } stats)
        {
            var s = stats.Split(',', StringSplitOptions.TrimEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            missions = missions.Select(m => m.For(m.Scenario with { Crafter = "Custom", Craftsmanship = s[0], Control = s[1], Cp = s[2] })).ToList();
            ownStats = false; // the Raphael plans in the scenarios were solved for the scenario's own stats
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"Clock: the simulator's action times x {MasterMissions.Pace:F2}, {MasterMissions.OverheadSeconds:F0} s between items. Points per item are player-reported; the rest is from the game's sheets.");
        var clock = Stopwatch.StartNew();

        foreach (var mission in missions)
        {
            var scenario = mission.Scenario;
            var recipe = scenario.Recipe;
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"## {mission.Name} - {recipe.Item}: progress {recipe.Progress}, quality {recipe.Quality} (ratings at {string.Join(" / ", recipe.CollectableTiers!)}), durability {recipe.Durability}; crafter {scenario.Craftsmanship}/{scenario.Control}/{scenario.Cp}");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{mission.Seconds} s, {mission.StartUses} uses of {mission.Duty}; checkpoints {string.Join(", ", mission.Checkpoints.Select(c => $"{c.Score} (+{c.Seconds} s, +{c.Uses})"))}.");

            // 1. Tune on the mission score.
            var tuned = start;
            if (tuneRuns > 0)
            {
                report.AppendLine();
                report.AppendLine(CultureInfo.InvariantCulture, $"Tuning on {tuneRuns} missions per setting (seeds 1..{tuneRuns}):");
                tuned = ExpertTuner.Tune(
                    tuning => MasterMissions.Scores(mission, tuning, 1, tuneRuns), start, line => report.AppendLine("- " + line),
                    scores => string.Create(CultureInfo.InvariantCulture, $"score {scores.Average() * 1000:F0}"));
                report.AppendLine(CultureInfo.InvariantCulture, $"- result: {tuned.Changes()}");
            }

            // 2. Measure on missions nobody was tuned on.
            report.AppendLine();
            report.AppendLine("| Who | Missions | Mean score | Lowest tenth | Checkpoint 1 | Checkpoint 2 | Items | Points per item | Top rating | Seconds per item | Duty uses |");
            report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");

            if (otherRuns > 0)
            {
                var settings = new EvaluatorSettings { MaxDepth = 2, Budget = TimeSpan.MaxValue };
                foreach (var contestant in HephaestusVersusArtisanBenchmark.Contestants(scenario, settings))
                {
                    if (!ownStats && !contestant.Name.Contains("Expert Recipe Solver", StringComparison.Ordinal))
                        continue;
                    Row(contestant.Name, Missions(otherRuns, seed => MasterMissions.Play(mission, seed, contestant.Seat)));
                }
            }

            Row("Sandbox policy, default knobs, no duty action", Missions(runs, seed => MasterMissions.Play(mission, start.With(Knob.DutyUses, 0), seed)));
            Row("Sandbox policy, default knobs", Missions(runs, seed => MasterMissions.Play(mission, start, seed)));
            MissionResult[]? alone = null;
            if (tuneRuns > 0)
            {
                Row("Sandbox policy, tuned, no duty action", Missions(runs, seed => MasterMissions.Play(mission, tuned.With(Knob.DutyUses, 0), seed)));
                alone = Missions(runs, seed => MasterMissions.Play(mission, tuned, seed));
                Row("Sandbox policy, tuned for this mission", alone);
            }

            if (samples > 0)
            {
                // A second is worth what the tuned policy makes in one: points before the multipliers, over the time they took.
                alone ??= Missions(runs, seed => MasterMissions.Play(mission, tuned, seed));
                var rate = alone.Sum(m => (double)m.Points) / Math.Max(1.0, alone.Sum(m => m.Seconds));
                var search = new SearchSettings(samples, Margin: 0.015, TimeValue: rate);
                var overrules = new ConcurrentDictionary<string, int>();
                Row(string.Create(CultureInfo.InvariantCulture, $"Sandbox policy + search ({samples} futures, a second = {rate:F1} points)"),
                    Missions(searchRuns, seed => MasterMissions.Play(mission, tuned, seed, search, overrules)));

                report.AppendLine();
                report.AppendLine("What the search took out of the policy's hands most often (condition: planned > pressed):");
                foreach (var (what, count) in overrules.OrderByDescending(o => o.Value).Take(16))
                    report.AppendLine(CultureInfo.InvariantCulture, $"- {count}x {what}");
            }

            // The road a real craft takes in the plugin: the preset for the recipe's shape, then the book's own tuning
            // (per craft: points less time, one use of the duty action), then the search with the plugin's settings.
            var book = new ExpertTuningBook(work =>
            {
                work();
                return Task.CompletedTask;
            });
            var preset = ExpertTuningBook.Preset(mission.Problem);
            Row("PLUGIN: the preset alone", Missions(runs, seed => MasterMissions.Play(mission, preset, seed)));
            var own = book.For(mission.Problem, tune: true, threads: Environment.ProcessorCount)();
            Row("PLUGIN: preset, then its own tuning", Missions(runs, seed => MasterMissions.Play(mission, own, seed)));
            if (samples > 0)
            {
                var plugin = new SearchSettings(samples, Margin: 0.015, TimeValue: ExpertTuningBook.TimeValue);
                Row(string.Create(CultureInfo.InvariantCulture, $"PLUGIN: its own tuning + search ({samples} futures)"),
                    Missions(searchRuns, seed => MasterMissions.Play(mission, own, seed, plugin)));
            }

            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"The plugin's own tuning: {book.Status}");

            void Row(string who, MissionResult[] results)
            {
                var n = results.Length;
                var items = Math.Max(1, results.Sum(r => r.Crafts));
                var sorted = results.Select(r => r.Score).OrderBy(x => x).ToArray();
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {who} | {n} | {results.Average(r => r.Score):F0} | {sorted[n / 10]} | {results.Count(r => r.Stage >= 1) * 100.0 / n:F0}% | {results.Count(r => r.Stage >= 2) * 100.0 / n:F0}% | {results.Average(r => r.Crafts):F1} | {results.Sum(r => r.Points) / (double)items:F0} | {results.Sum(r => r.Gold) * 100.0 / items:F0}% | {results.Sum(r => r.Seconds) / items:F0} | {results.Average(r => r.DutyUsed):F1} |");
            }
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Whole run: {clock.Elapsed.TotalSeconds:F0} s.");
        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }

    /// <summary>
    /// What MISSION-LEVEL play could add - the solver knows nothing of the mission around the item: not which items
    /// the duty action is best spent on (the later ones count double), not that the clock is about to cut an item
    /// off. Measured, not built: the plugin's presets, the policy alone (HEPHAESTUS_MISSION_SEARCH for the search).
    /// </summary>
    [BenchmarkFact]
    public void MissionLevelPlay_WhatIsThereToGain()
    {
        var runs = Setting("HEPHAESTUS_MISSION_RUNS", 400);
        var samples = Setting("HEPHAESTUS_MISSION_SEARCH", 0);
        MasterMissions.OverheadSeconds = Setting("HEPHAESTUS_MISSION_OVERHEAD", 6);
        MasterMissions.Pace = Setting("HEPHAESTUS_MISSION_PACE", 100) / 100.0;
        var report = new StringBuilder();
        foreach (var mission in MasterMission.All)
        {
            var tuning = ExpertTuningBook.Preset(mission.Problem);
            var search = samples > 0 ? new SearchSettings(samples, Margin: 0.015, TimeValue: ExpertTuningBook.TimeValue) : null;
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"## {mission.Name}: {mission.StartUses} uses of {mission.Duty}, +{string.Join(" / +", mission.Checkpoints.Select(c => c.Uses))}");
            report.AppendLine();
            report.AppendLine("| The duty action is lent | Mean score | Items | Uses spent | Seconds left over | Worth of that tail, at the mission's own rate |");
            report.AppendLine("|---|---|---|---|---|---|");
            var lending = new (string Name, int PerCraft, Func<int, int, int>? Lend)[]
            {
                ("one an item, while there are any (as built)", 1, null),
                ("one an item, from the first checkpoint on", 1, (stage, _) => stage >= 1 ? 1 : 0),
                ("one an item, from the second checkpoint on", 1, (stage, _) => stage >= 2 ? 1 : 0),
                ("one an item; two from the second checkpoint on", 2, (stage, _) => stage >= 2 ? 2 : 1),
                ("two an item, while there are any", 2, null),
            };
            foreach (var (name, perCraft, lend) in lending)
            {
                var knobs = tuning.With(Knob.DutyUses, perCraft);
                var results = Missions(runs, seed => MasterMissions.Play(mission, knobs, seed, search, lend: lend));
                var rate = results.Sum(r => (double)r.Score) / Math.Max(1.0, results.Sum(r => r.Seconds));
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {name} | {results.Average(r => r.Score):F0} | {results.Average(r => r.Crafts):F1} | {results.Average(r => r.DutyUsed):F1} | {results.Average(r => r.Leftover):F0} | {results.Average(r => r.Leftover) * rate:F0} ({results.Average(r => r.Leftover) * rate * 100 / Math.Max(1.0, results.Average(r => r.Score)):F1}% of the score) |");
            }
        }

        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }

    private static MissionResult[] Missions(int count, Func<int, MissionResult> play)
    {
        var results = new MissionResult[count];
        Parallel.For(0, count, i => results[i] = play(ValidationSeed + i));
        return results;
    }

    private static int Setting(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;
}
