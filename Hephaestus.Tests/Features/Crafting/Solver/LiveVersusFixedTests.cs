using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using static Hephaestus.Tests.Features.Crafting.Solver.SolverFixtures;

namespace Hephaestus.Tests.Features.Crafting.Solver;

/// <summary>
/// The reason the plugin exists, as a measurement: the same recipe, the same Raphael rotation, the
/// same dice — once played as a fixed rotation (what a macro or a static solver does), once with
/// the live evaluator choosing every step. Seeds are fixed, so these are deterministic; the margins
/// are deliberately well inside what was measured, so tuning can move the numbers without breaking
/// the point. If a change makes live LOSE to fixed, that change is wrong.
///
/// Measured 2026-09-19 (300 runs): weak stats — fixed 5907, live depth 1 7252, live depth 2 8972.
/// Full stats — fixed mean 11362 / worst 9024, live depth 2 mean 11393 / worst 10366.
/// </summary>
public class LiveVersusFixedTests
{
    private static EvaluatorSettings Depth(int depth) => new() { MaxDepth = depth, Budget = TimeSpan.MaxValue };

    [Fact]
    public void WithHeadroom_LiveBeatsTheFixedRotation_Clearly()
    {
        var fixedRotation = RotationPlayer.Sample(WeakStats, Baseline, NormalRecipe, runs: 2000, seed: 11);
        var live = PolicyPlayer.Sample(WeakStats, Baseline, NormalRecipe, runs: 120, seed: 11, Depth(1));

        Assert.Equal(1.0, live.CompletionRate);
        Assert.True(live.MeanQuality > fixedRotation.MeanQuality * 1.10,
            $"live {live.MeanQuality:F0} vs fixed {fixedRotation.MeanQuality:F0}");
    }

    [Fact]
    public void LookingFurtherAhead_IsWorthMore()
    {
        var shallow = PolicyPlayer.Sample(WeakStats, Baseline, NormalRecipe, runs: 40, seed: 11, Depth(1));
        var deeper = PolicyPlayer.Sample(WeakStats, Baseline, NormalRecipe, runs: 40, seed: 11, Depth(2));

        Assert.Equal(1.0, deeper.CompletionRate);
        Assert.True(deeper.MeanQuality > shallow.MeanQuality,
            $"depth 2 {deeper.MeanQuality:F0} vs depth 1 {shallow.MeanQuality:F0}");
    }

    /// <summary>
    /// When the plan already caps quality there is nothing to gain on average — what is left to win
    /// is the bad day: a Poor landing on the big finisher. Live must not make the worst case worse.
    /// </summary>
    [Fact]
    public void WithoutHeadroom_LiveProtectsTheWorstCase()
    {
        var fixedRotation = RotationPlayer.Sample(FullStats, Baseline, NormalRecipe, runs: 2000, seed: 11);
        var live = PolicyPlayer.Sample(FullStats, Baseline, NormalRecipe, runs: 40, seed: 11, Depth(2));

        Assert.Equal(1.0, live.CompletionRate);
        Assert.True(live.MinQuality >= fixedRotation.MinQuality,
            $"live worst {live.MinQuality} vs fixed worst {fixedRotation.MinQuality}");
        Assert.True(live.MeanQuality >= fixedRotation.MeanQuality * 0.99,
            $"live {live.MeanQuality:F0} vs fixed {fixedRotation.MeanQuality:F0}");
    }

    /// <summary>Whatever it chooses, it must never choose its way into a broken item.</summary>
    [Fact]
    public void LiveNeverBreaksAnItemTheFixedRotationCompletes()
    {
        foreach (var parameters in new[] { FullStats, WeakStats })
        {
            var live = PolicyPlayer.Sample(parameters, Baseline, NormalRecipe, runs: 80, seed: 99, Depth(1));

            Assert.Equal(1.0, live.CompletionRate);
        }
    }
}
