using System.Diagnostics;
using System.Globalization;
using System.Text;
using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Planner;
using Hephaestus.Tests.Sandbox.Expert;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Versus;
using Xunit.Abstractions;

namespace Hephaestus.Tests.Sandbox.Planner;

/// <summary>
/// Can <see cref="RotationPlanner"/> stand where raphael-cli stands? Recipe by recipe: its plan beside Raphael's
/// on paper (steps, quality under Normal conditions), then both plans handed to the plugin's live evaluator and
/// played on the same dice. A report, not a gate.
///
///   HEPHAESTUS_BENCHMARK=1 dotnet test Hephaestus.Tests -c Release --no-build \
///     --filter FullyQualifiedName~PlannerBenchmark --logger "console;verbosity=detailed"
///
/// Optional: HEPHAESTUS_PLANNER_CRAFTS (crafts per live row, default 300; 0 = on paper only),
/// HEPHAESTUS_PLANNER_BEAM (default 3000), HEPHAESTUS_PLANNER_POLISH (edits per run, default 1000000), HEPHAESTUS_BENCHMARK_ONLY (scenario names; default every ordinary one),
/// HEPHAESTUS_BENCHMARK_OUT (also write the report here).
/// </summary>
[Trait("Category", "Benchmark")]
public class PlannerBenchmark
{
    private readonly ITestOutputHelper _output;

    public PlannerBenchmark(ITestOutputHelper output) => _output = output;

    [BenchmarkFact]
    public void OurPlanner_BesideRaphael()
    {
        var crafts = Setting("HEPHAESTUS_PLANNER_CRAFTS", 300);
        var beam = Setting("HEPHAESTUS_PLANNER_BEAM", 3000);
        var polish = Setting("HEPHAESTUS_PLANNER_POLISH", 1_000_000); // edits per restart; 0 = the beam's rotation unpolished
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_ONLY")?.Split(',', StringSplitOptions.TrimEntries);
        var scenarios = VersusScenarios.All.Where(s => only is null ? !s.Recipe.Expert : only.Contains(s.Name)).ToList();
        var settings = new EvaluatorSettings { MaxDepth = 2, Budget = TimeSpan.MaxValue };

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Beam {beam}; live rows: {crafts} crafts on seeds 100001.., the plugin's live evaluator on each plan.");
        report.AppendLine("Full quality = a certain HQ. On paper = Normal conditions throughout.");

        // HEPHAESTUS_SANDBOX_SPECIALIST=1: the crafter may use Heart and Soul and Quick Innovation (Raphael's rows stay as solved without).
        var specialist = Hephaestus.Tests.Sandbox.Expert.SandboxProblems.SpecialistByDefault;
        foreach (var scenario in scenarios)
        {
            var parameters = scenario.Parameters;
            if (specialist)
            {
                parameters = parameters with
                {
                    AllowedActions = ActionMask.With(ActionMask.With(parameters.AllowedActions, CraftAction.HeartAndSoul), CraftAction.QuickInnovation),
                };
            }

            var recipe = scenario.Recipe;
            var target = scenario.TargetQuality;

            var clock = Stopwatch.StartNew();
            var ours = RotationPlanner.Plan(parameters, scenario.InitialQuality, target, beam, polish);
            var seconds = clock.Elapsed.TotalSeconds;

            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"## {scenario.Name}: progress {recipe.Progress}, quality {target} (starting at {scenario.InitialQuality}), durability {recipe.Durability}, crafter {scenario.Craftsmanship}/{scenario.Control}/{scenario.Cp}");
            report.AppendLine();
            report.AppendLine("| Plan | Steps on paper | Quality on paper | Live: full quality | Live: HQ items | Live: mean quality | Live: mean steps |");
            report.AppendLine("|---|---|---|---|---|---|---|");
            Row("Raphael", scenario.Plan);
            Row("Raphael, progress last (what Artisan asks for)", scenario.BackloadedPlan);
            Row(string.Create(CultureInfo.InvariantCulture, $"Ours ({seconds:F1} s to find)"), ours.Actions);

            // THE PLUGIN AS IT NOW DECIDES (planner = our own): our rotation and the live evaluator when the rotation
            // reaches the target on paper, else the expert solver - tuned by its book, time priced by what the craft is for.
            if (crafts > 0)
            {
                var request = new BaselineRequest(
                    recipe.RecipeLevel, recipe.Progress, recipe.Quality, recipe.Durability, false, scenario.JobLevel, parameters.BaseProgress,
                    parameters.BaseQuality, scenario.Craftsmanship, scenario.Control, scenario.Cp, scenario.InitialQuality, true, specialist, specialist, false);
                var planClock = Stopwatch.StartNew();
                var own = OwnPlan.Solve(request, threads: Math.Max(1, Environment.ProcessorCount / 2));
                var planSeconds = planClock.Elapsed.TotalSeconds;
                var reaches = OwnPlan.Carries(parameters, own.Actions, scenario.InitialQuality, target, recipe.CanHq);
                foreach (var (intent, timeValue) in new[] { ("quality first", 0.0), ("bulk", ExpertTuningBook.BulkTimeValue) })
                {
                    var problem = new ExpertProblem(scenario.Name, parameters, recipe.ConditionsFlag, scenario.InitialQuality, target, TimeValue: timeValue, ByHqChance: recipe.CanHq);
                    var tuning = new ExpertTuningBook(work =>
                    {
                        work();
                        return Task.CompletedTask;
                    }).For(problem, tune: true, threads: Environment.ProcessorCount)();
                    var search = new SearchSettings(128, Margin: timeValue > 0 ? 0.015 : 0.03, TimeValue: timeValue);
                    var runs = new VersusResult[crafts];
                    Parallel.For(0, crafts, i => runs[i] = VersusPlayer.Play(
                        parameters, recipe.ConditionsFlag, scenario.InitialQuality, 100_001 + i,
                        reaches
                            ? new HephaestusDriver(parameters, own.Actions, recipe.ConditionsFlag, settings)
                            : new ExpertPolicyDriver(problem, tuning, search, seed: i + 1)));
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"| PLUGIN NOW, {intent}: {(reaches ? "our rotation + live evaluator" : "the expert solver")} (planned in {planSeconds:F1} s on half the cores) | {own.Steps} | {own.Quality}{(own.Reached ? "" : " (short)")} | {runs.Count(r => r.Delivered(target)) * 100.0 / crafts:F1}% | {HqItems(runs, parameters):F1}% | {runs.Average(r => r.Quality):F0} | {runs.Average(r => r.Steps):F1} |");
                    if (reaches)
                        break; // the intent only matters to the expert solver
                }
            }

            // What the plugin is measured against in the end: Artisan plays Raphael's plan as a fixed macro.
            if (crafts > 0 && HephaestusVersusArtisanBenchmark.Contestants(scenario, settings).FirstOrDefault(c => c.Name == "Artisan: Raphael") is { } artisan)
            {
                var runs = new VersusResult[crafts];
                Parallel.For(0, crafts, i => runs[i] = VersusPlayer.Play(
                    parameters, recipe.ConditionsFlag, scenario.InitialQuality, 100_001 + i, artisan.Seat(scenario)));
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| ARTISAN: Raphael's plan as a macro | {scenario.BackloadedPlan.Length} | | {runs.Count(r => r.Delivered(target)) * 100.0 / crafts:F1}% | {HqItems(runs, parameters):F1}% | {runs.Average(r => r.Quality):F0} | {runs.Average(r => r.Steps):F1} |");
            }
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"Ours: {string.Join(" > ", ours.Actions)}");

            void Row(string name, CraftAction[] plan)
            {
                var paper = RotationPlayer.PlayNormal(parameters, plan);
                var quality = Math.Min(parameters.MaxQuality, paper.Quality + scenario.InitialQuality);
                var live = "| | | |";
                if (crafts > 0)
                {
                    var runs = new VersusResult[crafts];
                    Parallel.For(0, crafts, i => runs[i] = VersusPlayer.Play(
                        parameters, recipe.ConditionsFlag, scenario.InitialQuality, 100_001 + i,
                        new HephaestusDriver(parameters, plan, recipe.ConditionsFlag, settings)));
                    live = string.Create(CultureInfo.InvariantCulture,
                        $"{runs.Count(r => r.Delivered(target)) * 100.0 / crafts:F1}% | {HqItems(runs, parameters):F1}% | {runs.Average(r => r.Quality):F0} | {runs.Average(r => r.Steps):F1} |");
                }

                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {name} | {paper.StepsUsed}{(paper.Completed ? "" : " (NOT finished)")} | {quality}{(quality >= target ? "" : " (short)")} | {live}");
            }
        }

        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }

    /// <summary>
    /// Where the polisher gets to from each starting rotation ALONE - the beam's, each textbook shape, and Raphael's own
    /// plans (never used as a start by the planner; here they show whether the search can at least HOLD the best known
    /// rotation, i.e. whether its weakness is the search or the starting points).
    /// </summary>
    [BenchmarkFact]
    public void ThePolisher_StartByStart()
    {
        var polish = Setting("HEPHAESTUS_PLANNER_POLISH", 1_000_000);
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_ONLY")?.Split(',', StringSplitOptions.TrimEntries);
        var report = new StringBuilder();
        foreach (var scenario in VersusScenarios.All.Where(s => only is null ? !s.Recipe.Expert : only.Contains(s.Name)))
        {
            var parameters = scenario.Parameters;
            var target = scenario.TargetQuality;
            report.AppendLine(CultureInfo.InvariantCulture, $"## {scenario.Name} (Raphael on paper: {scenario.Plan.Length} steps)");
            var starts = new List<(string Name, CraftAction[] Plan)>
            {
                ("the beam's", RotationPlanner.SolveBest(parameters, scenario.InitialQuality, target).Actions),
            };
            for (var i = 0; i < PlanPolisher.Shapes.Length; i++)
                starts.Add(($"shape {i + 1}", PlanPolisher.Shapes[i]));
            starts.Add(("Raphael's plan", scenario.Plan));
            starts.Add(("Raphael's, progress last", scenario.BackloadedPlan));

            foreach (var (name, plan) in starts)
            {
                var result = PlanPolisher.Polish(parameters, scenario.InitialQuality, target, plan, polish, restarts: 4, shapes: false);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"- from {name}: {result.Steps} steps, quality {result.Quality}{(result.Reached ? "" : " (short)")} - {string.Join(" > ", result.Actions)}");
            }
        }

        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }

    /// <summary>High-quality items per hundred crafts, as the game deals them: each finished item by its HQ chance at the quality it ended on.</summary>
    private static double HqItems(VersusResult[] runs, CraftParameters parameters)
        => runs.Average(r => r.Completed ? HqChance.Percent(r.Quality, parameters.MaxQuality) : 0.0);

    private static int Setting(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;
}
