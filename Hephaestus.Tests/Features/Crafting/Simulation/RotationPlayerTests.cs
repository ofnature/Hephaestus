using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Tests.Features.Crafting.Simulation;

public class RotationPlayerTests
{
    // Black Star Mask of Casting (Raphael's in-game-verified case), with a finisher added so it completes.
    private static readonly CraftParameters Mask = new(601, 80, 6300, 11400, 238, 300, 94, ActionMask.All);

    private static readonly CraftAction[] QualityThenFinish =
    {
        Reflect, Innovation, PreparatoryTouch, PrudentTouch, GreatStrides, PreparatoryTouch,
        MasterMend, WasteNot2, Veneration, Groundwork, Groundwork, Groundwork, Groundwork,
        Veneration, Groundwork,
    };

    [Fact]
    public void PlayNormal_MatchesSteppingTheSimulatorByHand()
    {
        var result = RotationPlayer.PlayNormal(Mask, new[] { Reflect, Innovation, PreparatoryTouch, PrudentTouch });

        Assert.Equal(2610, result.Quality); // Raphael vector d11d9c68, step 4
        Assert.Equal(4, result.StepsUsed);
        Assert.Equal(0, result.StepsSkipped);
        Assert.False(result.Completed);
    }

    /// <summary>A macro line that errors is skipped in game; so is an unusable step here.</summary>
    [Fact]
    public void UnusableSteps_AreSkipped_NotFatal()
    {
        var result = RotationPlayer.PlayNormal(Mask, new[] { BasicTouch, PreciseTouch, BasicTouch });

        Assert.Equal(2, result.StepsUsed);
        Assert.Equal(1, result.StepsSkipped);
    }

    [Fact]
    public void Sample_IsDeterministicForASeed()
    {
        var first = RotationPlayer.Sample(Mask, QualityThenFinish, ConditionOdds.NormalRecipeFlags, runs: 500, seed: 42);
        var second = RotationPlayer.Sample(Mask, QualityThenFinish, ConditionOdds.NormalRecipeFlags, runs: 500, seed: 42);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// On a normal recipe conditions only scale quality, so a rotation that completes under Normal
    /// completes every time — while its quality spreads out. That spread is exactly what a plan
    /// that cannot react leaves on the table, and what the live evaluator is for.
    /// </summary>
    [Fact]
    public void FixedRotation_UnderRealDice_AlwaysCompletes_ButQualityVaries()
    {
        var normal = RotationPlayer.PlayNormal(Mask, QualityThenFinish);
        Assert.True(normal.Completed);

        var rolled = RotationPlayer.Sample(Mask, QualityThenFinish, ConditionOdds.NormalRecipeFlags, runs: 5000, seed: 1);

        Assert.Equal(1.0, rolled.CompletionRate);
        Assert.True(rolled.MinQuality < normal.Quality, "a Poor on a big touch should cost quality");
        Assert.True(rolled.MaxQuality > normal.Quality, "a Good/Excellent on a big touch should add quality");
        Assert.True(rolled.MeanQuality > normal.Quality, "Good (x1.5, 25%) outweighs Poor (x0.5, only after a 4% Excellent)");
    }

    /// <summary>An action that can fail makes a fixed rotation unreliable — half of these never finish.</summary>
    [Fact]
    public void RapidSynthesis_MakesCompletionAGamble()
    {
        var parameters = Mask with { MaxProgress = 1190 }; // exactly one Rapid Synthesis (238 x 5)
        var summary = RotationPlayer.Sample(parameters, new[] { RapidSynthesis }, ConditionOdds.NormalRecipeFlags, runs: 4000, seed: 3);

        Assert.InRange(summary.CompletionRate, 0.46, 0.54);
    }

    [Fact]
    public void Sample_ZeroRuns_IsEmptyNotACrash()
    {
        Assert.Equal(0, RotationPlayer.Sample(Mask, QualityThenFinish, ConditionOdds.NormalRecipeFlags, runs: 0, seed: 1).Runs);
    }
}
