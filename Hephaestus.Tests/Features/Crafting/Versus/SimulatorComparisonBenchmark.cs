using System.Globalization;
using System.Text;
using Hephaestus.Tests.Features.Crafting.Solver;
using Xunit.Abstractions;

namespace Hephaestus.Tests.Features.Crafting.Versus;

/// <summary>
/// Where Artisan's simulator and ours answer differently — a report (see <see cref="SimulatorComparison"/>).
///
///   HEPHAESTUS_BENCHMARK=1 dotnet test Hephaestus.Tests -c Release --no-build \
///     --filter FullyQualifiedName~SimulatorComparisonBenchmark --logger "console;verbosity=detailed"
/// </summary>
[Trait("Category", "Benchmark")]
public class SimulatorComparisonBenchmark
{
    private readonly ITestOutputHelper _output;

    public SimulatorComparisonBenchmark(ITestOutputHelper output) => _output = output;

    [BenchmarkFact]
    public void WhereTheTwoSimulatorsDisagree()
    {
        var report = new StringBuilder();
        report.AppendLine("Artisan's simulator vs Hephaestus's, one action at a time from identical states (2,000 random crafts per scenario).");
        foreach (var scenario in VersusScenarios.All.Where(s => s.Name is "Ingot770-Lean" or "Reinforcement754-Solid" or "OizysRarities759-Solid"))
        {
            var differences = SimulatorComparison.Run(scenario, crafts: 2000, seed: 1);
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"## {scenario.Name}: {differences.Count} difference(s)");
            foreach (var group in differences.GroupBy(d => (d.Kind, d.Action)).OrderByDescending(g => g.Count()).Take(40))
            {
                var example = group.First();
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"- {group.Key.Kind} after {group.Key.Action}: {group.Count()}x, e.g. under {example.Condition} ours {example.Ours} / Artisan {example.Theirs} from [{example.State}]");
            }
        }

        _output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("HEPHAESTUS_BENCHMARK_OUT") is { Length: > 0 } path)
            File.WriteAllText(path, report.ToString());
    }
}
