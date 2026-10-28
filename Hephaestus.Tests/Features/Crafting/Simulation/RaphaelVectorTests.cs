using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Tests.Features.Crafting.Simulation;

/// <summary>
/// Rotations with known outcomes, ported from raphael-sim's tests/simulator_tests.rs
/// (KonaeAkira/raphael-rs, Apache-2.0). Independent ground truth: if these pass, potencies, costs,
/// buff timing, combo pricing and Inner Quiet all agree with a simulator whose results include
/// rotations verified in game. The "ingame" cases are the ones Raphael marks as checked in game.
/// </summary>
public class RaphaelVectorTests
{
    private static CraftParameters Parameters(int cp, int durability, int progress, int quality, int baseProgress, int baseQuality, int level)
        => new(cp, durability, progress, quality, baseProgress, baseQuality, level, ActionMask.All);

    private static CraftState Play(CraftParameters parameters, params CraftAction[] rotation)
    {
        var state = CraftSimulator.Initial(parameters);
        foreach (var action in rotation)
        {
            Assert.Equal(ActionError.None, CraftSimulator.Use(parameters, state, action, Condition.Normal, true, out var next));
            state = next;
        }

        return state;
    }

    private static List<(int Progress, int Quality)> Trace(CraftParameters parameters, params (CraftAction Action, Condition Condition)[] steps)
    {
        var state = CraftSimulator.Initial(parameters);
        var trace = new List<(int, int)>();
        foreach (var (action, condition) in steps)
        {
            Assert.Equal(ActionError.None, CraftSimulator.Use(parameters, state, action, condition, true, out var next));
            state = next;
            trace.Add((state.Progress, state.Quality));
        }

        return trace;
    }

    /// <summary>Copper Gorget at level 10 — below level 11 there is no Inner Quiet.</summary>
    [Fact]
    public void Random_926ae85b_LowLevel_NoInnerQuiet()
    {
        var state = Play(Parameters(50, 60, 33, 150, 4, 38, 10), BasicSynthesis, BasicTouch, BasicTouch);

        Assert.Equal(14, state.Cp);
        Assert.Equal(30, state.Durability);
        Assert.Equal(4, state.Progress);
        Assert.Equal(76, state.Quality);
        Assert.Equal(0, state.InnerQuiet);
    }

    /// <summary>Ironwood Spear — Muscle Memory + Veneration + Waste Not + Manipulation timing.</summary>
    [Fact]
    public void Random_3c721e47()
    {
        var state = Play(Parameters(500, 80, 3100, 6800, 240, 307, 85),
            MuscleMemory, Veneration, WasteNot, Groundwork, Manipulation, Innovation, PreparatoryTouch, PrudentTouch);

        Assert.Equal(223, state.Cp);
        Assert.Equal(60, state.Durability);
        Assert.Equal(2520, state.Progress);
        Assert.Equal(1473, state.Quality);
    }

    /// <summary>Grade 4 Skybuilders' Stone — the Basic→Standard combo price and buff countdowns.</summary>
    [Fact]
    public void Random_3ba90d3a()
    {
        var state = Play(Parameters(427, 60, 1080, 9900, 204, 253, 81),
            Veneration, CarefulSynthesis, CarefulSynthesis, PreparatoryTouch, MasterMend,
            Innovation, PrudentTouch, BasicTouch, StandardTouch);

        Assert.Equal(188, state.Cp);
        Assert.Equal(25, state.Durability);
        Assert.Equal(918, state.Progress);
        Assert.Equal(2118, state.Quality);
        Assert.Equal(5, state.InnerQuiet);
        Assert.Equal(1, state.Innovation);
    }

    /// <summary>Diadochos Wristband — a full 26-step rotation ending on 1 CP and 5 durability.</summary>
    [Fact]
    public void Random_bce2650c_FullRotation()
    {
        var state = Play(Parameters(700, 70, 6600, 14040, 248, 270, 90),
            MuscleMemory, WasteNot, Veneration, Groundwork, Groundwork, Groundwork, PrudentSynthesis, MasterMend,
            PrudentTouch, Innovation, PrudentTouch, PrudentTouch, PrudentTouch, PrudentTouch, MasterMend,
            Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, GreatStrides, Innovation,
            Observe, AdvancedTouch, GreatStrides, ByregotsBlessing);

        Assert.Equal(1, state.Cp);
        Assert.Equal(5, state.Durability);
        Assert.Equal(6323, state.Progress);
        Assert.Equal(11475, state.Quality);
    }

    /// <summary>Classical Index — checked in game, including an Excellent and the Poor that follows it.</summary>
    [Fact]
    public void Ingame_be9fc5c2_WithConditions()
    {
        var trace = Trace(Parameters(594, 70, 3900, 10920, 247, 265, 90),
            (Reflect, Condition.Normal),
            (Manipulation, Condition.Normal),
            (PreciseTouch, Condition.Excellent),
            (WasteNot, Condition.Poor),
            (PreparatoryTouch, Condition.Normal),
            (MasterMend, Condition.Normal),
            (PreparatoryTouch, Condition.Normal),
            (Innovation, Condition.Normal),
            (BasicTouch, Condition.Normal),
            (StandardTouch, Condition.Normal),
            (AdvancedTouch, Condition.Normal),
            (PrudentTouch, Condition.Normal),
            (GreatStrides, Condition.Normal),
            (Innovation, Condition.Normal),
            (Observe, Condition.Normal),
            (AdvancedTouch, Condition.Normal),
            (Veneration, Condition.Normal),
            (Groundwork, Condition.Normal),
            (DelicateSynthesis, Condition.Normal));

        Assert.Equal(
            new[]
            {
                (0, 795), (0, 795), (0, 2703), (0, 2703), (0, 3445), (0, 3445), (0, 4293), (0, 4293),
                (0, 5008), (0, 5952), (0, 7144), (0, 7939), (0, 7939), (0, 7939), (0, 7939), (0, 9926),
                (0, 9926), (1333, 9926), (1703, 10456),
            },
            trace);
    }

    /// <summary>Black Star Mask of Casting — checked in game on patch 7.0; ends exactly on max quality.</summary>
    [Fact]
    public void Ingame_d11d9c68()
    {
        var parameters = Parameters(601, 80, 6300, 11400, 238, 300, 94);
        var trace = Trace(parameters,
            (Reflect, Condition.Normal), (Innovation, Condition.Normal), (PreparatoryTouch, Condition.Normal),
            (PrudentTouch, Condition.Normal), (GreatStrides, Condition.Normal), (PreparatoryTouch, Condition.Normal),
            (GreatStrides, Condition.Normal), (Innovation, Condition.Normal), (PreparatoryTouch, Condition.Normal),
            (MasterMend, Condition.Normal), (GreatStrides, Condition.Normal), (ByregotsBlessing, Condition.Normal));

        Assert.Equal(
            new[]
            {
                (0, 900), (0, 900), (0, 1980), (0, 2610), (0, 2610), (0, 4860),
                (0, 4860), (0, 4860), (0, 7410), (0, 7410), (0, 7410), (0, 11400),
            },
            trace);
    }

    /// <summary>
    /// Maraging Steel Ingot at level 100 — checked in game on patch 7.1. The only in-game case with
    /// Refined Touch, a Manipulation recast and Trained Perfection feeding a Preparatory Touch.
    /// </summary>
    [Fact]
    public void Ingame_4866545e()
    {
        var parameters = Parameters(540, 35, 4125, 12000, 282, 256, 100);
        var trace = Trace(parameters,
            (Reflect, Condition.Normal), (Manipulation, Condition.Normal), (Innovation, Condition.Normal),
            (BasicTouch, Condition.Normal), (RefinedTouch, Condition.Normal), (PrudentTouch, Condition.Normal),
            (PrudentTouch, Condition.Normal), (Innovation, Condition.Normal), (BasicTouch, Condition.Normal),
            (StandardTouch, Condition.Normal), (AdvancedTouch, Condition.Normal), (Manipulation, Condition.Normal),
            (TrainedPerfection, Condition.Normal), (GreatStrides, Condition.Normal), (Innovation, Condition.Normal),
            (PreparatoryTouch, Condition.Normal), (GreatStrides, Condition.Normal), (ByregotsBlessing, Condition.Normal));

        Assert.Equal(
            new[]
            {
                (0, 768), (0, 768), (0, 768), (0, 1228), (0, 1727), (0, 2303), (0, 2917), (0, 2917), (0, 3569),
                (0, 4433), (0, 5527), (0, 5527), (0, 5527), (0, 5527), (0, 5527), (0, 8087), (0, 8087), (0, 11927),
            },
            trace);
    }

    /// <summary>
    /// Rarefied Tacos de Carne Asada at level 100. Raphael runs this one in its adversarial mode, so
    /// only its PROGRESS column is comparable — which still pins Trained Perfection, Immaculate Mend
    /// and Veneration'd Groundwork at the level cap.
    /// </summary>
    [Fact]
    public void Ingame_f9f0dac7_ProgressColumn()
    {
        var parameters = Parameters(697, 80, 6600, 12000, 261, 240, 100);
        var state = Play(parameters,
            Reflect, Observe, AdvancedTouch, Innovation, BasicTouch, StandardTouch, AdvancedTouch, PreparatoryTouch,
            ImmaculateMend, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, Innovation,
            BasicTouch, StandardTouch, AdvancedTouch, ByregotsBlessing, TrainedPerfection, ImmaculateMend, Veneration);
        Assert.Equal(0, state.Progress);

        var progress = new List<int>();
        foreach (var action in new[] { Groundwork, Groundwork, Groundwork, Groundwork, Veneration, Groundwork })
        {
            Assert.Equal(ActionError.None, CraftSimulator.Use(parameters, state, action, Condition.Normal, true, out var next));
            state = next;
            progress.Add(state.Progress);
        }

        // Raphael's last row reads 7045: it does not cap progress, we cap at the recipe total.
        Assert.Equal(new[] { 1409, 2818, 4227, 5636, 5636, 6600 }, progress);
        Assert.True(CraftSimulator.IsSuccess(parameters, state));
    }

    /// <summary>
    /// Straight from the real solver: raphael-cli built from source at tag v0.28.6, asked for the
    /// Black Star Mask with its quality cap lifted (so nothing is hidden by clamping):
    /// <c>solve --custom-recipe 650 6300 30000 80 --override-base-increases 94 238 300
    /// --stats 3957 3896 601 --level 94 --manipulation</c>. It reported cp 4, durability 0,
    /// progress 6317, quality 11744 — the last step finishes the craft on its final durability.
    /// </summary>
    [Fact]
    public void RaphaelCli_v0_28_6_SolvedRotation_ReplaysToTheSameNumbers()
    {
        var parameters = Parameters(601, 80, 6300, 30000, 238, 300, 94);
        var state = Play(parameters,
            Reflect, Innovation, PrudentTouch, PrudentTouch, BasicTouch, StandardTouch, Manipulation, Innovation,
            WasteNot2, BasicTouch, StandardTouch, AdvancedTouch, Veneration, Groundwork, Groundwork, Groundwork,
            Groundwork, Innovation, Veneration, DelicateSynthesis, GreatStrides, ByregotsBlessing, CarefulSynthesis);

        Assert.Equal(4, state.Cp);
        Assert.Equal(0, state.Durability);
        Assert.Equal(11744, state.Quality);
        Assert.Equal(6300, state.Progress); // the CLI reports 6317; we cap at the recipe total
        Assert.True(CraftSimulator.IsSuccess(parameters, state));
    }

    /// <summary>The same two recipes' base values, recomputed from the stats Raphael quotes for them.</summary>
    [Fact]
    public void BaseValues_MatchTheStatsQuotedForClassicalIndex()
    {
        // Level 90 crafter on a level 90 recipe: divider 130 / modifier 80 (progress), 115 / 70 (quality).
        Assert.Equal(247, CraftParameters.ComputeBaseProgress(4000, 90, 90, 130, 80));
        Assert.Equal(265, CraftParameters.ComputeBaseQuality(3962, 90, 90, 115, 70));
    }

    [Fact]
    public void BaseValues_ModifierOnlyAppliesWhenNotAboveTheRecipeLevel()
    {
        Assert.Equal(309, CraftParameters.ComputeBaseProgress(4000, 100, 90, 130, 80));
        Assert.Equal(379, CraftParameters.ComputeBaseQuality(3962, 100, 90, 115, 70));
    }
}
