using Hephaestus.Features.Crafting.Expert;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Artisan.CraftingLogic.Solvers;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Tests.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Versus;
using Xunit.Abstractions;

namespace Hephaestus.Tests.Sandbox.Expert;

/// <summary>
/// The sandbox's bench: tune <see cref="ExpertPolicy"/> on simulated crafts, then measure it - alone
/// and under <see cref="ExpertSearch"/> - against Artisan's Expert Recipe Solver on crafts NEITHER
/// was tuned on (validation seeds start at 100,001; tuning uses 1..N). A report, not a gate.
///
///   HEPHAESTUS_BENCHMARK=1 dotnet test Hephaestus.Tests -c Release --no-build \
///     --filter FullyQualifiedName~ExpertSandboxBenchmark --logger "console;verbosity=detailed"
///
/// Optional: HEPHAESTUS_SANDBOX_TUNE (crafts per recipe per setting tried, default 2000; 0 = no tuning),
/// HEPHAESTUS_SANDBOX_VALIDATE (crafts per row, default 2000), HEPHAESTUS_SANDBOX_SEARCH (futures per
/// candidate, default 0 = no search rows), HEPHAESTUS_SANDBOX_SEARCH_CRAFTS (crafts per search row,
/// default 200), HEPHAESTUS_SANDBOX_TRACES (failed crafts printed action by action, default 2),
/// HEPHAESTUS_BENCHMARK_ONLY (scenario names), HEPHAESTUS_BENCHMARK_OUT (also write the report here),
/// HEPHAESTUS_SANDBOX_STATS ("craftsmanship,control,cp": a real character instead of the assumed one).
/// </summary>
[Trait("Category", "Benchmark")]
public class ExpertSandboxBenchmark
{
    private const int ValidationSeed = 100_001;

    private readonly ITestOutputHelper _output;

    public ExpertSandboxBenchmark(ITestOutputHelper output) => _output = output;

    [BenchmarkFact]
    public void ExpertSandbox_TuneThenMeasure()
    {
        var tuneCrafts = Setting("HEPHAESTUS_SANDBOX_TUNE", 2000);
        var timeValue = Setting("HEPHAESTUS_SANDBOX_TIME", 0);    // points a second; a delivered item is worth 1,000
        var rivals = Setting("HEPHAESTUS_SANDBOX_RIVALS", 1) > 0; // 0 = leave out the rows that are not ours
        var crafts = Setting("HEPHAESTUS_SANDBOX_VALIDATE", 2000);
        var samples = Setting("HEPHAESTUS_SANDBOX_SEARCH", 0);
        var searchCrafts = Math.Min(crafts, Setting("HEPHAESTUS_SANDBOX_SEARCH_CRAFTS", 200));
        var traces = Setting("HEPHAESTUS_SANDBOX_TRACES", 2);
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_ONLY")?.Split(',', StringSplitOptions.TrimEntries);

        // Expert recipes by default; a recipe NAMED in HEPHAESTUS_BENCHMARK_ONLY is played whatever it is - which is
        // how "could our own solver make the ordinary recipes too, without Raphael?" is measured.
        var scenarios = VersusScenarios.All.Where(s => only is null ? s.Recipe.Expert : only.Contains(s.Name)).ToList();
        var report = new StringBuilder();

        // A real character instead of the assumed "Solid" one: both sides get the same stats (neither uses the scenario's Raphael plans).
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_SANDBOX_STATS") is { Length: > 0 } stats)
        {
            var s = stats.Split(',', StringSplitOptions.TrimEntries).Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            scenarios = scenarios.Select(x => x with { Crafter = "Custom", Craftsmanship = s[0], Control = s[1], Cp = s[2] }).ToList();
            report.AppendLine(CultureInfo.InvariantCulture, $"Crafter: {s[0]} craftsmanship / {s[1]} control / {s[2]} CP (HEPHAESTUS_SANDBOX_STATS)");
            report.AppendLine();
        }

        var problems = scenarios.Select(SandboxProblems.From).ToList();
        var clock = Stopwatch.StartNew();

        // 1. Tune: one setting for all the recipes together, then one per recipe starting from it.
        // HEPHAESTUS_SANDBOX_START: where the tuning starts from ("Knob=value,..."). One knob at a time cannot leave a
        // STYLE (gambling on Rapid Synthesis, say, for Waste Not and Groundwork), so a second start can find what the first cannot.
        var general = Knobs(Environment.GetEnvironmentVariable("HEPHAESTUS_SANDBOX_START"));
        var perRecipe = problems.ToDictionary(p => p.Name, _ => general);
        if (tuneCrafts > 0)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"# Tuning ({tuneCrafts} crafts per recipe per setting tried, seeds 1..{tuneCrafts})");
            report.AppendLine();
            report.AppendLine("All recipes together:");
            general = ExpertTuner.Tune(problems, general, tuneCrafts, line => report.AppendLine("- " + line), timeValue: timeValue);
            report.AppendLine(CultureInfo.InvariantCulture, $"- result: {general.Changes()}");
            foreach (var problem in problems)
            {
                report.AppendLine();
                report.AppendLine(CultureInfo.InvariantCulture, $"{problem.Name} alone, from there:");
                perRecipe[problem.Name] = ExpertTuner.Tune(new[] { problem }, general, tuneCrafts, line => report.AppendLine("- " + line), timeValue: timeValue);
                report.AppendLine(CultureInfo.InvariantCulture, $"- result: {perRecipe[problem.Name].Changes()}");
            }

            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"Tuning took {clock.Elapsed.TotalSeconds:F0} s.");
            report.AppendLine();
        }

        // 2. Measure on crafts nobody was tuned on.
        report.AppendLine(CultureInfo.InvariantCulture,
            $"# Measured on {crafts} fresh crafts per row (seeds {ValidationSeed}..); search rows on the first {searchCrafts}");
        report.AppendLine("Delivered = finished at the demanded quality. Same seed = the same condition on every step for every row.");
        var search = samples > 0 ? new SearchSettings(samples, TimeValue: timeValue) : null;
        if (timeValue > 0)
            report.AppendLine(CultureInfo.InvariantCulture, $"A second costs {timeValue} points; a delivered item is worth 1,000.");
        foreach (var (scenario, problem) in scenarios.Zip(problems))
        {
            var recipe = scenario.Recipe;
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"## {scenario.Name}: {recipe.Item} (rlvl {recipe.RecipeLevel}), progress {recipe.Progress}, quality {problem.Target}, durability {recipe.Durability}, crafter {scenario.Craftsmanship}/{scenario.Control}/{scenario.Cp}");
            report.AppendLine();
            report.AppendLine("| Who | Crafts | Delivered | Mean quality | Mean steps | Ahead of Artisan | Behind Artisan |");
            report.AppendLine("|---|---|---|---|---|---|---|");

            // maxCosmic: Artisan's "max quality on Cosmic recipes" setting, on as it ships; off = it stops at the top rating.
            ExpertRun[] PlayArtisan(bool maxCosmic)
            {
                var runs = new ExpertRun[crafts];
                Parallel.For(0, crafts, i =>
                {
                    var solver = new ExpertSolver();
                    solver.ActiveProfile.Settings.MaxCosmicRecipes = maxCosmic;
                    var run = VersusPlayer.Play(scenario.Parameters, recipe.ConditionsFlag, scenario.InitialQuality, ValidationSeed + i,
                        new ArtisanDriver(ArtisanDriver.Craft(scenario), solver));
                    runs[i] = new ExpertRun(run.Completed, run.Quality, run.Steps, 0, run.Final);
                });
                return runs;
            }

            ExpertRun[] artisan;
            if (!rivals)
            {
                artisan = Play(problem, ExpertTuning.Default, crafts, null); // our own untuned row is the yardstick then
            }
            else if (recipe.Expert)
            {
                artisan = PlayArtisan(maxCosmic: true);
                Row("Artisan: Expert Recipe Solver", artisan, artisan);
                if (recipe.CollectableTiers != null)
                    Row("Artisan: Expert Recipe Solver, aiming at the top rating", PlayArtisan(maxCosmic: false), artisan);
            }
            else
            {
                // An ordinary recipe: the yardstick of the last two columns is the plugin as it is - a Raphael plan, re-scored live.
                artisan = null!;
                var settings = new Hephaestus.Features.Crafting.Solver.EvaluatorSettings { MaxDepth = 2, Budget = TimeSpan.MaxValue };
                foreach (var contestant in HephaestusVersusArtisanBenchmark.Contestants(scenario, settings))
                {
                    var runs = new ExpertRun[crafts];
                    Parallel.For(0, crafts, i =>
                    {
                        var run = VersusPlayer.Play(scenario.Parameters, recipe.ConditionsFlag, scenario.InitialQuality, ValidationSeed + i, contestant.Seat(scenario));
                        runs[i] = new ExpertRun(run.Completed, run.Quality, run.Steps, 0, run.Final);
                    });
                    artisan ??= runs;
                    Row(contestant.Name + (ReferenceEquals(artisan, runs) ? " [the yardstick]" : ""), runs, artisan);
                }
            }

            Row("Sandbox policy, untuned", Play(problem, ExpertTuning.Default, crafts, null), artisan);
            if (tuneCrafts > 0)
            {
                Row("Sandbox policy, tuned for all recipes", Play(problem, general, crafts, null), artisan);
                Row("Sandbox policy, tuned for this recipe", Play(problem, perRecipe[problem.Name], crafts, null), artisan);
            }

            var overrules = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
            if (search != null)
            {
                Row($"Sandbox policy + search ({samples} futures), all-recipe tuning", Play(problem, general, searchCrafts, search, overrules), artisan);
                if (tuneCrafts > 0)
                    Row($"Sandbox policy + search ({samples} futures), this recipe's tuning", Play(problem, perRecipe[problem.Name], searchCrafts, search), artisan);
            }

            // Where the search disagrees with the policy most: each line is a rule the policy does not have yet.
            if (!overrules.IsEmpty)
            {
                report.AppendLine();
                report.AppendLine("What the search took out of the policy's hands most often (all-recipe tuning; condition: planned > pressed):");
                foreach (var (what, count) in overrules.OrderByDescending(o => o.Value).Take(18))
                    report.AppendLine(CultureInfo.InvariantCulture, $"- {count}x {what}");
            }

            // How the tuned policy's lost crafts ended, and a few of them action by action.
            var tuned = perRecipe[problem.Name];
            var lost = Play(problem, tuned, crafts, null).Select((run, i) => (run, Seed: ValidationSeed + i)).Where(x => !x.run.Delivered(problem.Target)).ToList();
            if (lost.Count > 0)
            {
                var finished = lost.Where(x => x.run.Completed).ToList();
                report.AppendLine();
                report.Append(CultureInfo.InvariantCulture, $"The tuned policy alone lost {lost.Count}: {lost.Count - finished.Count} broke or stalled");
                if (finished.Count > 0)
                {
                    report.Append(CultureInfo.InvariantCulture,
                        $", {finished.Count} finished short (by {problem.Target - finished.Average(x => x.run.Quality):F0} quality on average, {finished.Average(x => x.run.Final.Cp):F0} CP and {finished.Average(x => x.run.Final.Durability):F0} durability unspent)");
                }

                report.AppendLine(".");
                foreach (var (_, seed) in lost.Take(traces))
                {
                    var trace = VersusPlayer.Play(scenario.Parameters, recipe.ConditionsFlag, scenario.InitialQuality, seed, new ExpertPolicyDriver(problem, tuned));
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"- seed {seed}, quality {trace.Quality}{(trace.Completed ? "" : ", NOT finished")}: {HephaestusVersusArtisanBenchmark.Trace(trace)}");
                }
            }

            void Row(string who, ExpertRun[] runs, ExpertRun[] rival)
            {
                var n = runs.Length;
                var delivered = runs.Count(r => r.Delivered(problem.Target)) * 100.0 / n;
                var error = Math.Sqrt(delivered * (100 - delivered) / n);
                var ahead = Enumerable.Range(0, n).Count(i => runs[i].Delivered(problem.Target) && !rival[i].Delivered(problem.Target)) * 100.0 / n;
                var behind = Enumerable.Range(0, n).Count(i => !runs[i].Delivered(problem.Target) && rival[i].Delivered(problem.Target)) * 100.0 / n;
                var overruled = runs.Sum(r => r.Overruled) is var count and > 0
                    ? string.Create(CultureInfo.InvariantCulture, $" ({count * 100.0 / Math.Max(1, runs.Sum(r => r.Steps)):F0}% of steps overruled)")
                    : "";
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"| {who}{overruled} | {n} | {delivered:F1}% ± {error:F1} | {runs.Average(r => r.Quality):F0} | {runs.Average(r => r.Steps):F1} | {(ReferenceEquals(runs, rival) ? "" : $"{ahead:F1}%")} | {(ReferenceEquals(runs, rival) ? "" : $"{behind:F1}%")} |");
            }
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Whole run: {clock.Elapsed.TotalSeconds:F0} s.");
        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }

    /// <summary>
    /// One craft under the magnifying glass: every step with the state it was taken from. For reading WHY the
    /// policy pressed what it pressed. HEPHAESTUS_SANDBOX_SEED (default 100001), HEPHAESTUS_BENCHMARK_ONLY
    /// (one scenario; default the first expert one), HEPHAESTUS_SANDBOX_KNOBS ("Knob=value,Knob=value" over the defaults).
    /// </summary>
    [BenchmarkFact]
    public void ExpertSandbox_OneCraftStepByStep()
    {
        var seed = Setting("HEPHAESTUS_SANDBOX_SEED", ValidationSeed);
        var uses = Setting("HEPHAESTUS_SANDBOX_DUTY", 0);
        var only = Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_ONLY");
        var scenario = VersusScenarios.All.First(s => string.IsNullOrEmpty(only) ? s.Recipe.Expert : s.Name == only);
        var problem = MasterMission.All.FirstOrDefault(m => m.Scenario == scenario)?.Problem ?? SandboxProblems.From(scenario);
        var tuning = Knobs(Environment.GetEnvironmentVariable("HEPHAESTUS_SANDBOX_KNOBS"));
        var parameters = problem.Parameters;

        var random = new Random(seed);
        var odds = ConditionOdds.ForRecipe(problem.ConditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(problem.ConditionsFlag);
        var state = ExpertPlayer.Start(problem, uses);
        var condition = Condition.Normal;
        var quarters = 0;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{scenario.Name}, seed {seed}, {tuning.Changes()}; progress {parameters.MaxProgress}, quality {problem.Target}, base {parameters.BaseProgress}/{parameters.BaseQuality}, duty uses {uses}");
        for (var step = 1; step <= 200 && !CraftSimulator.IsFinal(parameters, state); step++)
        {
            var action = ExpertPolicy.Decide(parameters, tuning, problem.Aim(tuning), state, condition, problem.Tiers);
            var success = random.Next(100) < CraftSimulator.SuccessPercent(state, action, condition);
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{step,3} {quarters / 4.0,5:F1}s P {state.Progress,5} Q {state.Quality,5} CP {state.Cp,3} D {state.Durability,2} IQ {state.InnerQuiet,2} inno {state.Innovation} gs {state.GreatStrides} ven {state.Veneration} mm {state.MuscleMemory} manip {state.Manipulation} wn {state.WasteNot} sh {state.SteadyHand} miracle {state.MaterialMiracle / 4.0:F0}s | {condition,-9} {action}{(success ? "" : " (failed)")}");
            if (CraftSimulator.Use(parameters, state, action, condition, success, out var next) != ActionError.None)
                break;

            quarters += CraftSimulator.QuarterSeconds(action);
            state = next;
            if (CraftSimulator.SpendsStep(action))
                condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle(), state.MaterialMiracle > 0);
        }

        text.AppendLine(CultureInfo.InvariantCulture,
            $"end P {state.Progress} Q {state.Quality} ({state.Quality * 100 / parameters.MaxQuality}%) CP {state.Cp} D {state.Durability}, {quarters / 4.0:F1} s, points {problem.Points(CraftSimulator.IsSuccess(parameters, state), state.Quality)}");
        _output.WriteLine(text.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, text.ToString());
    }

    /// <summary>"Knob=value,Knob=value" laid over the defaults.</summary>
    internal static ExpertTuning Knobs(string? text)
    {
        var tuning = ExpertTuning.Default;
        foreach (var pair in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=');
            tuning = tuning.With(Enum.Parse<Knob>(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        return tuning;
    }

    private static ExpertRun[] Play(
        ExpertProblem problem, ExpertTuning tuning, int crafts, SearchSettings? search,
        System.Collections.Concurrent.ConcurrentDictionary<string, int>? overrules = null)
    {
        var runs = new ExpertRun[crafts];
        Parallel.For(0, crafts, i => runs[i] = ExpertPlayer.Play(problem, tuning, ValidationSeed + i, search, overrules));
        return runs;
    }

    private static int Setting(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= 0 ? value : fallback;
}
