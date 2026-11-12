using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Tests.Features.Crafting.Solver;

public class RaphaelCommandTests
{
    // Black Star Mask of Casting, the stats Raphael's in-game-verified test uses.
    private static readonly BaselineRequest Mask = new(
        RecipeLevel: 650, MaxProgress: 6300, MaxQuality: 11400, MaxDurability: 80, Expert: false,
        JobLevel: 94, BaseProgress: 238, BaseQuality: 300, Craftsmanship: 3957, Control: 3896, MaxCp: 601,
        InitialQuality: 0, Manipulation: true, HeartAndSoul: false, QuickInnovation: false, BackloadProgress: false);

    /// <summary>This exact command line was run against the v0.28.6 binary we build; it is what produced our test baseline.</summary>
    [Fact]
    public void Arguments_MatchTheInvocationVerifiedAgainstTheRealBinary()
    {
        Assert.Equal(
            "solve --custom-recipe 650 6300 11400 80 0 --override-base-increases 94 238 300 "
            + "--stats 3957 3896 601 --level 94 --manipulation --threads 4 --output-variables actions",
            RaphaelCommand.Arguments(Mask, threads: 4));
    }

    [Fact]
    public void OptionalFlags_AppearOnlyWhenSet()
    {
        var args = RaphaelCommand.Arguments(
            Mask with { Expert = true, InitialQuality = 1200, HeartAndSoul = true, QuickInnovation = true, BackloadProgress = true, Manipulation = false },
            threads: 0);

        Assert.Contains("--custom-recipe 650 6300 11400 80 1 ", args);
        Assert.Contains("--initial-quality 1200", args);
        Assert.Contains("--heart-and-soul", args);
        Assert.Contains("--quick-innovation", args);
        Assert.Contains("--backload-progress", args);
        Assert.DoesNotContain("--manipulation", args);
        Assert.DoesNotContain("--threads", args);
    }

    /// <summary>More threads make the answer arrive sooner, not differ — so they must not split the cache.</summary>
    [Fact]
    public void CacheKey_IgnoresThreadCount_ButNothingElse()
    {
        Assert.Equal(RaphaelCommand.CacheKey(Mask), RaphaelCommand.CacheKey(Mask));
        Assert.DoesNotContain("--threads", RaphaelCommand.CacheKey(Mask));
        Assert.NotEqual(RaphaelCommand.CacheKey(Mask), RaphaelCommand.CacheKey(Mask with { MaxCp = 602 }));
        Assert.NotEqual(RaphaelCommand.CacheKey(Mask), RaphaelCommand.CacheKey(Mask with { InitialQuality = 500 }));
    }

    /// <summary>A decimal comma on a non-English Windows must never leak into the command line.</summary>
    [Fact]
    public void Arguments_AreCultureInvariant()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Contains("--custom-recipe 650 6300 11400 80 0", RaphaelCommand.Arguments(Mask, 4));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    /// <summary>The literal stdout of the real binary for the request above.</summary>
    [Fact]
    public void ParseActions_ReadsTheRealBinarysOutput()
    {
        const string stdout =
            "\"[MuscleMemory, Manipulation, Veneration, WasteNot2, Groundwork, Groundwork, Groundwork, BasicTouch, RefinedTouch, "
            + "DelicateSynthesis, Innovation, PreparatoryTouch, BasicTouch, StandardTouch, AdvancedTouch, Innovation, "
            + "DelicateSynthesis, TrainedFinesse, GreatStrides, ByregotsBlessing, BasicSynthesis]\"\r\n";

        Assert.Equal(SolverFixtures.Baseline, RaphaelCommand.ParseActions(stdout));
    }

    [Fact]
    public void ParseActions_FindsTheLineAmongLogNoise()
    {
        var parsed = RaphaelCommand.ParseActions("[2026-09-19T18:00:00Z INFO raphael] solving...\n\"[Reflect, BasicSynthesis]\"\n");

        Assert.Equal(new[] { CraftAction.Reflect, CraftAction.BasicSynthesis }, parsed);
    }

    /// <summary>A rotation we cannot replay faithfully is worse than no baseline at all.</summary>
    [Theory]
    [InlineData("\"[MuscleMemory, StellarSteadyHand, BasicSynthesis]\"")] // a Cosmic duty action: modelled, but not one the plugin can press
    [InlineData("\"[MuscleMemory, 7, BasicSynthesis]\"")]                 // a number is not an action name
    [InlineData("error: the solver ran out of memory")]
    [InlineData("")]
    public void ParseActions_RejectsWhatItCannotTrust(string stdout)
    {
        Assert.Null(RaphaelCommand.ParseActions(stdout));
    }

    [Fact]
    public void ParseActions_AnEmptyRotation_IsEmptyNotNull()
    {
        Assert.Empty(RaphaelCommand.ParseActions("\"[]\"")!);
    }
}
