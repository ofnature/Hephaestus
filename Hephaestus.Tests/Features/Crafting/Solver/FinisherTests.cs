using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using static Hephaestus.Tests.Features.Crafting.Solver.SolverFixtures;

namespace Hephaestus.Tests.Features.Crafting.Solver;

public class FinisherTests
{
    [Fact]
    public void FromAFreshCraft_ItCompletes_WithoutTouchingQuality()
    {
        var final = Finisher.Run(FullStats, CraftSimulator.Initial(FullStats), out var steps);

        Assert.True(CraftSimulator.IsSuccess(FullStats, final));
        Assert.Equal(0, final.Quality);
        Assert.InRange(steps, 1, 30);
    }

    [Fact]
    public void WhenOneStepFinishes_ItTakesTheCheapestOne()
    {
        var almost = CraftSimulator.Initial(FullStats) with { Progress = FullStats.MaxProgress - 100 };
        var used = new List<CraftAction>();

        var final = Finisher.Run(FullStats, almost, out _, used);

        Assert.True(CraftSimulator.IsSuccess(FullStats, final));
        Assert.Equal(new[] { CraftAction.BasicSynthesis }, used); // 0 CP beats Careful Synthesis' 7
    }

    /// <summary>The last step may spend the last durability — finishing and breaking at once is a finish.</summary>
    [Fact]
    public void TheFinishingBlow_MaySpendTheLastDurability()
    {
        var state = CraftSimulator.Initial(FullStats) with { Progress = FullStats.MaxProgress - 100, Durability = 10 };

        Assert.True(CraftSimulator.IsSuccess(FullStats, Finisher.Run(FullStats, state, out _)));
    }

    [Fact]
    public void WhenEveryStepWouldBreakTheItem_ItMendsFirst()
    {
        var state = CraftSimulator.Initial(FullStats) with { Durability = 5 }; // even Prudent Synthesis costs 5
        var used = new List<CraftAction>();

        var final = Finisher.Run(FullStats, state, out _, used);

        Assert.True(CraftSimulator.IsSuccess(FullStats, final));
        Assert.Equal(CraftAction.MasterMend, used[0]);
    }

    [Fact]
    public void WithManipulationRunning_ItWaitsForDurability_InsteadOfPayingForAMend()
    {
        var state = CraftSimulator.Initial(FullStats) with { Durability = 5, Manipulation = 6 };
        var used = new List<CraftAction>();

        Finisher.Run(FullStats, state, out _, used);

        Assert.Equal(CraftAction.Observe, used[0]);
    }

    /// <summary>Ten durability is not an emergency yet: Prudent Synthesis (5) still fits, so no CP goes on a mend.</summary>
    [Fact]
    public void WhileACheapStepStillFits_ItDoesNotMendEarly()
    {
        var state = CraftSimulator.Initial(FullStats) with { Durability = 10 };
        var used = new List<CraftAction>();

        Finisher.Run(FullStats, state, out _, used);

        Assert.NotEqual(CraftAction.MasterMend, used[0]);
    }

    /// <summary>It must say so when the craft is lost — the evaluator prices lines by this answer.</summary>
    [Fact]
    public void WhenItCannotBeDone_ItDoesNotPretend()
    {
        var hopeless = CraftSimulator.Initial(FullStats) with { Durability = 5, Cp = 0 };

        Assert.False(CraftSimulator.IsSuccess(FullStats, Finisher.Run(FullStats, hopeless, out _)));
    }

    [Fact]
    public void ItNeverUsesAnActionThatCanFail()
    {
        var used = new List<CraftAction>();

        Finisher.Run(FullStats, CraftSimulator.Initial(FullStats), out _, used);

        Assert.All(used, action => Assert.Equal(100, CraftSimulator.SuccessPercent(action, Condition.Normal)));
    }
}
