using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Tests.Features.Crafting.Simulation;

/// <summary>
/// Cosmic Exploration's two duty actions. The first thing pinned is that they do not exist for an
/// ordinary craft: nothing lends the charges, and the plugin's masks leave them out.
/// </summary>
public class CosmicDutyActionTests
{
    private static readonly CraftParameters P = new(
        MaxCp: 600, MaxDurability: 80, MaxProgress: 10000, MaxQuality: 20000,
        BaseProgress: 100, BaseQuality: 100, JobLevel: 100, AllowedActions: ActionMask.All);

    private static CraftState Use(CraftState state, CraftAction action, Condition condition = Condition.Normal, bool success = true)
    {
        Assert.Equal(ActionError.None, CraftSimulator.Use(P, state, action, condition, success, out var next));
        return next;
    }

    private static CraftState Start(byte steady = 0, byte miracle = 0)
    {
        var state = CraftSimulator.Initial(P);
        state.SteadyHandCharges = steady;
        state.MaterialMiracleCharges = miracle;
        return state;
    }

    [Fact]
    public void WithoutAMission_NeitherActionCanBeUsed()
    {
        var state = CraftSimulator.Initial(P); // even with every action allowed: no charges
        Assert.Equal(ActionError.NoRemainingUses, CraftSimulator.Check(P, state, StellarSteadyHand, Condition.Normal));
        Assert.Equal(ActionError.NoRemainingUses, CraftSimulator.Check(P, state, MaterialMiracle, Condition.Normal));

        var regular = P with { AllowedActions = ActionMask.Regular };
        var lent = Start(steady: 1, miracle: 1); // and with charges, the regular mask still says no
        Assert.Equal(ActionError.NotAllowed, CraftSimulator.Check(regular, lent, StellarSteadyHand, Condition.Normal));
        Assert.Equal(ActionError.NotAllowed, CraftSimulator.Check(regular, lent, MaterialMiracle, Condition.Normal));
    }

    [Fact]
    public void StellarSteadyHand_TakesAStepAndACharge_AndTheNextThreeStepsCannotFail()
    {
        var state = Use(Use(Start(steady: 2), Veneration), StellarSteadyHand);

        Assert.Equal(1, state.SteadyHandCharges);
        Assert.Equal(3, state.SteadyHand);
        Assert.Equal(3, state.Veneration); // it took a step: Veneration ticked
        Assert.Equal(0, CraftSimulator.CpCost(state, StellarSteadyHand, Condition.Normal));
        Assert.Equal(100, CraftSimulator.SuccessPercent(state, RapidSynthesis, Condition.Normal));
        Assert.Equal(50, CraftSimulator.SuccessPercent(RapidSynthesis, Condition.Normal));

        // The dice say "failed" three times; the fourth time they are believed again.
        for (var i = 0; i < 3; i++)
        {
            var before = state.Progress;
            state = Use(state, RapidSynthesis, success: false);
            Assert.True(state.Progress > before, $"step {i + 1} under Stellar Steady Hand failed");
        }

        Assert.Equal(0, state.SteadyHand);
        var progress = state.Progress;
        state = Use(state, RapidSynthesis, success: false);
        Assert.Equal(progress, state.Progress);
    }

    [Fact]
    public void MaterialMiracle_IsInstant_TakesACharge_AndRunsOnTheClock()
    {
        var state = Use(Use(Start(miracle: 2), Veneration), MaterialMiracle);

        Assert.False(CraftSimulator.SpendsStep(MaterialMiracle));
        Assert.Equal(1, state.MaterialMiracleCharges);
        Assert.Equal(CraftSimulator.MaterialMiracleQuarterSeconds, state.MaterialMiracle);
        Assert.Equal(4, state.Veneration); // no step: Veneration did not tick
        Assert.Equal(ActionError.ConditionNotMet, CraftSimulator.Check(P, state, MaterialMiracle, Condition.Normal)); // not while one is running

        // 45 seconds: a synthesis takes 2.5 of them, a buff 1.25.
        state = Use(state, BasicSynthesis);
        Assert.Equal(170, state.MaterialMiracle);
        state = Use(state, Innovation);
        Assert.Equal(165, state.MaterialMiracle);

        for (var i = 0; i < 40 && state.MaterialMiracle > 0; i++)
            state = Use(state, Observe);
        Assert.Equal(0, state.MaterialMiracle);
        Assert.Equal(ActionError.None, CraftSimulator.Check(P, state, MaterialMiracle, Condition.Normal)); // the second charge
    }

    [Fact]
    public void MaterialMiracle_OpensTheCraft_WithoutSpendingTheFirstStep()
    {
        var state = Use(Start(miracle: 1), MaterialMiracle);
        Assert.Equal(Combo.SynthesisBegin, state.Combo);
        Assert.Equal(ActionError.None, CraftSimulator.Check(P, state, Reflect, Condition.Normal));
    }

    [Fact]
    public void UnderMaterialMiracle_EveryFreeRollIsOneOfItsSixConditions()
    {
        // A recipe that deals only Normal, Good and Good Omen (flag 515) - the miracle does not care.
        var odds = ConditionOdds.ForRecipe(515, 100);
        var seen = new HashSet<Condition>();
        for (var i = 0; i < 600; i++)
            seen.Add(ConditionOdds.Next(odds, expert: true, Condition.Normal, i / 600f, materialMiracle: true));

        Assert.Equal(
            new[] { Condition.Good, Condition.Centered, Condition.Sturdy, Condition.Pliant, Condition.Malleable, Condition.Primed }.OrderBy(c => c),
            seen.OrderBy(c => c));

        // Forced transitions still come first, and without the miracle nothing changes.
        Assert.Equal(Condition.Good, ConditionOdds.Next(odds, true, Condition.GoodOmen, 0.99f, materialMiracle: true));
        for (var i = 0; i < 20; i++)
            Assert.Equal(ConditionOdds.Next(odds, true, Condition.Normal, i / 20f), ConditionOdds.Next(odds, true, Condition.Normal, i / 20f, materialMiracle: false));
    }
}
