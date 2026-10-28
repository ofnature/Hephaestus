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
///
/// The last two tests are recipes where <see cref="LiveVersusFixedBenchmark"/> (200 paired seeds)
/// caught live losing, before the search shared BaselineTracker's hard rule and the drift yardstick
/// was fixed. All of them failed on that code. Paired dice there: one seed drives both players.
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

    private static (PlayResult Fixed, PlayResult Live) Paired(BenchmarkRecipe recipe, int seed)
        => (RotationPlayer.PlayRolled(recipe.Parameters, recipe.Baseline, recipe.ConditionsFlag, new Random(seed)),
            PolicyPlayer.Play(recipe.Parameters, recipe.Baseline, recipe.ConditionsFlag, new Random(seed), Depth(2)));

    /// <summary>
    /// Plans that cap (or all but cap) quality, on seeds live used to end BELOW the fixed rotation:
    /// it must now match or beat it on every one. BlackStar94-OtherPlan is real raphael-cli output;
    /// the rest are hand-made benchmark plans.
    /// </summary>
    [Theory]
    [InlineData("BlackStar94-OtherPlan", new[] { 61, 65, 87, 93, 153, 157, 187 })]
    [InlineData("Tacos100-Dur80", new[] { 3, 6, 8, 13, 16, 17, 19, 22 })]
    [InlineData("Skybuilders81-Dur60", new[] { 8, 9, 13, 16, 19, 21, 24, 27 })]
    [InlineData("Mid70", new[] { 21, 22, 27, 35, 43 })]
    public void WhereLiveUsedToLose_ItNoLongerEndsBelowTheFixedRotation(string name, int[] seeds)
    {
        var recipe = BenchmarkRecipes.Named(name);
        foreach (var seed in seeds)
        {
            var (fixedRun, live) = Paired(recipe, seed);

            Assert.True(live.Completed, $"seed {seed}: live broke the item");
            Assert.True(live.Quality >= fixedRun.Quality, $"seed {seed}: live {live.Quality} vs fixed {fixedRun.Quality}");
        }
    }

    /// <summary>
    /// Plans with headroom, seeds 1-8, where live used to lose on the worst case (and on the mean
    /// for the Diadochos plans). Measured after the fixes: Diadochos90 13910 / worst 13294 vs fixed
    /// 13078 / 11938; TightCp 9669 / 8153 vs 8088 / 7167; Maraging 11065 / 10097 vs 10460 / 9674.
    /// </summary>
    [Theory]
    [InlineData("Diadochos90")]
    [InlineData("Diadochos90-TightCp")]
    [InlineData("Maraging100-Dur35")]
    public void WithHeadroom_WhereLiveUsedToLose_ItNowWinsOnMeanAndWorstCase(string name)
    {
        var recipe = BenchmarkRecipes.Named(name);
        var runs = Enumerable.Range(1, 8).Select(seed => Paired(recipe, seed)).ToList();

        Assert.All(runs, run => Assert.True(run.Live.Completed));
        var (fixedMean, liveMean) = (runs.Average(r => r.Fixed.Quality), runs.Average(r => r.Live.Quality));
        var (fixedWorst, liveWorst) = (runs.Min(r => r.Fixed.Quality), runs.Min(r => r.Live.Quality));
        Assert.True(liveMean >= fixedMean, $"live {liveMean:F0} vs fixed {fixedMean:F0}");
        Assert.True(liveWorst >= fixedWorst, $"live worst {liveWorst} vs fixed worst {fixedWorst}");
    }
}
