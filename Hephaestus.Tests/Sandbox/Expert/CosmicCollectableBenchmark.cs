using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Planner;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Versus;
using Xunit.Abstractions;

namespace Hephaestus.Tests.Sandbox.Expert;

/// <summary>
/// Cosmic mission items the game does NOT label Expert (EX missions and below): rated collectables all the same.
/// Two roads, the same dice: AS AN ORDINARY ITEM (the plugin until 2026-11-15: our planner, then the live evaluator
/// when the rotation fills the bar on paper, else the expert solver aiming at the full bar by HQ chance) and AS A
/// RATED ITEM (the expert solver at once, for its points, the clock at one price or another). The row marked
/// PLUGIN NOW is what CraftSetupReader hands the solver; the Master mission's price is there to show why it is not
/// used for these (one Component in six left unfinished). A report, not a gate.
///
///   HEPHAESTUS_BENCHMARK=1 dotnet test Hephaestus.Tests -c Release --no-build \
///     --filter FullyQualifiedName~CosmicCollectable --logger "console;verbosity=detailed"
///
/// Optional: HEPHAESTUS_SANDBOX_STATS ("craftsmanship,control,cp"; default the user's 5911,5503,641),
/// HEPHAESTUS_COSMIC_CRAFTS (crafts per row, default 300), HEPHAESTUS_BENCHMARK_ONLY (item names), HEPHAESTUS_BENCHMARK_OUT.
/// </summary>
[Trait("Category", "Benchmark")]
public class CosmicCollectableBenchmark
{
    /// <summary>
    /// From the game's sheets (client 2026.11.03, read 2026-11-15); the same row for every trade. Ratings are
    /// WKSMissionToDoEvalutionRefin percentages as quality. No Raphael plans: these are not in VersusScenarios.All.
    /// </summary>
    internal static readonly VersusRecipe[] Recipes =
    {
        // The two the user's EX missions of 2026-11-15 were made of: 87% and 42%, then 91 / 78 / 87 / 100%.
        new("Voyager Relic Restoration Component", 771, 100, 7600, 21100, 55, 170, 90, 150, 75, 15, Expert: false, CanHq: true, RequiredQuality: 0, CollectableTiers: new[] { 10550, 12660, 17930 }),
        new("Ruins Aetheric Gauge", 771, 100, 7200, 24100, 80, 170, 90, 150, 75, 15, Expert: false, CanHq: true, RequiredQuality: 0, CollectableTiers: new[] { 12050, 14460, 20480 }),
        // Two easy ones, which a planned rotation fills the bar of in 11 to 13 steps.
        new("Ruins Barrow", 701, 100, 1100, 12400, 60, 170, 90, 150, 75, 15, Expert: false, CanHq: true, RequiredQuality: 0, CollectableTiers: new[] { 4960, 6820, 8680 }),
        new("Survey Ruby Scythe", 690, 100, 3696, 14640, 80, 170, 90, 150, 75, 15, Expert: false, CanHq: true, RequiredQuality: 0, CollectableTiers: new[] { 4390, 7320, 10240 }),
    };

    private readonly ITestOutputHelper _output;

    public CosmicCollectableBenchmark(ITestOutputHelper output) => _output = output;

    [BenchmarkFact]
    public void RatedOrOrdinary()
    {
        var crafts = int.TryParse(Environment.GetEnvironmentVariable("HEPHAESTUS_COSMIC_CRAFTS"), out var asked) && asked > 0 ? asked : 300;
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_ONLY")?.Split(',', StringSplitOptions.TrimEntries);
        var stats = (Environment.GetEnvironmentVariable("HEPHAESTUS_SANDBOX_STATS") ?? "5911,5503,641")
            .Split(',', StringSplitOptions.TrimEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var settings = new EvaluatorSettings { MaxDepth = 2, Budget = TimeSpan.MaxValue };

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Crafter {stats[0]} / {stats[1]} / {stats[2]}; {crafts} crafts a row on the same seeds; seconds on the simulator's clock (the planner's wait is NOT in them).");

        foreach (var recipe in Recipes.Where(r => only is null || only.Contains(r.Item)))
        {
            var scenario = new VersusScenario(recipe.Item, recipe, "bench", stats[0], stats[1], stats[2], 0,
                Array.Empty<CraftAction>(), Array.Empty<CraftAction>(), null);
            var parameters = scenario.Parameters;
            var tiers = recipe.CollectableTiers!;

            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"## {recipe.Item} (rlvl {recipe.RecipeLevel}): progress {recipe.Progress}, quality {recipe.Quality}, durability {recipe.Durability}; ratings at {string.Join(" / ", tiers)}");
            report.AppendLine();
            report.AppendLine("| Road | Top rating | Under the lowest rating | Not finished | Quality: lowest tenth | median | Steps | Seconds | Points |");
            report.AppendLine("|---|---|---|---|---|---|---|---|---|");

            // As an ordinary item: plan first, and only a rotation that carries is followed.
            var request = new BaselineRequest(
                recipe.RecipeLevel, recipe.Progress, recipe.Quality, recipe.Durability, false, scenario.JobLevel, parameters.BaseProgress,
                parameters.BaseQuality, scenario.Craftsmanship, scenario.Control, scenario.Cp, 0, true, false, false, false);
            var planClock = Stopwatch.StartNew();
            var own = OwnPlan.Solve(request, threads: Math.Max(1, Environment.ProcessorCount / 2));
            var planSeconds = planClock.Elapsed.TotalSeconds;
            var carries = OwnPlan.Carries(parameters, own.Actions, 0, recipe.Quality, recipe.CanHq);
            var ordinary = new ExpertProblem(recipe.Item, parameters, recipe.ConditionsFlag, 0, recipe.Quality, ByHqChance: recipe.CanHq);

            // As a rated item, at a Master mission's price for the clock.
            var rated = new ExpertProblem(recipe.Item, parameters, recipe.ConditionsFlag, 0, tiers[^1], tiers, TimeValue: ExpertTuningBook.TimeValue);

            Row(string.Create(CultureInfo.InvariantCulture,
                    $"ordinary: {(carries ? $"our rotation ({own.Steps} steps) + live evaluator" : $"expert solver, full bar by HQ chance (rotation stops at {own.Quality})")}; planned in {planSeconds:F0} s"),
                seed => carries
                    ? new HephaestusDriver(parameters, own.Actions, recipe.ConditionsFlag, settings)
                    : new ExpertPolicyDriver(ordinary, Tuned(ordinary), new SearchSettings(128, Margin: 0.03, TimeValue: 0), seed));
            Row("rated as a Master mission's item: points, the clock at 10 a second",
                seed => new ExpertPolicyDriver(rated, Tuned(rated), new SearchSettings(128, Margin: 0.015, TimeValue: rated.TimeValue), seed));
            foreach (var price in new[] { 0.0, 2.0, 4.0 })
            {
                var points = rated with { TimeValue = price };
                Row(string.Create(CultureInfo.InvariantCulture, $"rated: points, the clock at {price:F0} a second{(price == ExpertTuningBook.RatedTimeValue ? " - PLUGIN NOW" : "")}"),
                    seed => new ExpertPolicyDriver(points, Tuned(points), new SearchSettings(128, Margin: price > 0 ? 0.015 : 0.03, TimeValue: price), seed));
            }

            // The top rating as a DEMAND: reach it and finish, nothing above it counts.
            foreach (var price in new[] { 0.0, 2.0 })
            {
                var demand = new ExpertProblem(recipe.Item, parameters, recipe.ConditionsFlag, 0, tiers[^1], TimeValue: price);
                Row(string.Create(CultureInfo.InvariantCulture, $"the top rating as a demand, the clock at {price:F0} a second"),
                    seed => new ExpertPolicyDriver(demand, Tuned(demand), new SearchSettings(128, Margin: price > 0 ? 0.015 : 0.03, TimeValue: price), seed));
            }

            void Row(string name, Func<int, IVersusDriver> seat)
            {
                var runs = new VersusResult[crafts];
                Parallel.For(0, crafts, i => runs[i] = VersusPlayer.Play(parameters, recipe.ConditionsFlag, 0, 100_001 + i, seat(i + 1)));
                var quality = runs.Where(r => r.Completed).Select(r => r.Quality).OrderBy(q => q).ToArray();
                var n = Math.Max(1, quality.Length);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {name} | {runs.Count(r => r.Delivered(tiers[^1])) * 100.0 / crafts:F1}% | {runs.Count(r => r.Completed && r.Quality < tiers[0]) * 100.0 / crafts:F1}% | {runs.Count(r => !r.Completed) * 100.0 / crafts:F1}% | {Percent(quality.ElementAtOrDefault(n / 10))}% | {Percent(quality.ElementAtOrDefault(n / 2))}% | {runs.Average(r => r.Steps):F1} | {runs.Average(Seconds):F0} | {runs.Average(r => rated.Points(r.Completed, r.Quality)):F0} |");
            }

            int Percent(int quality) => (int)((long)quality * 100 / recipe.Quality);
        }

        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }

    private static readonly Dictionary<string, ExpertTuning> Tunings = new();

    /// <summary>The plugin's tuning book, both stages, tuned where it is asked.</summary>
    private static ExpertTuning Tuned(ExpertProblem problem)
    {
        lock (Tunings)
        {
            var key = ExpertTuningBook.Key(problem);
            if (!Tunings.TryGetValue(key, out var tuning))
            {
                Tunings[key] = tuning = new ExpertTuningBook(work =>
                {
                    work();
                    return Task.CompletedTask;
                }).For(problem, tune: true, threads: Environment.ProcessorCount)();
            }

            return tuning;
        }
    }

    private static double Seconds(VersusResult run)
        => run.Trace.Sum(step => Enum.TryParse<CraftAction>(step.Action, out var action) ? CraftSimulator.QuarterSeconds(action) : 0) / 4.0;
}
