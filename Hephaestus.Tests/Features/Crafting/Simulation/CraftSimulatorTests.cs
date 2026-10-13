using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Tests.Features.Crafting.Simulation;

public class CraftSimulatorTests
{
    // Level 100, round base values so expected numbers can be read off the potencies.
    private static readonly CraftParameters P = new(
        MaxCp: 600, MaxDurability: 80, MaxProgress: 10000, MaxQuality: 20000,
        BaseProgress: 100, BaseQuality: 100, JobLevel: 100, AllowedActions: ActionMask.All);

    private static CraftState Use(CraftState state, CraftAction action, Condition condition = Condition.Normal, bool success = true)
        => Use(P, state, action, condition, success);

    private static CraftState Use(CraftParameters parameters, CraftState state, CraftAction action, Condition condition = Condition.Normal, bool success = true)
    {
        Assert.Equal(ActionError.None, CraftSimulator.Use(parameters, state, action, condition, success, out var next));
        return next;
    }

    private static ActionError Error(CraftState state, CraftAction action, Condition condition = Condition.Normal)
        => CraftSimulator.Check(P, state, action, condition);

    private static CraftState Start => CraftSimulator.Initial(P);

    // ---- start of synthesis --------------------------------------------------------------------

    [Fact]
    public void Initial_FullCpAndDurability_FirstStepActionsUsable()
    {
        var state = Start;

        Assert.Equal(600, state.Cp);
        Assert.Equal(80, state.Durability);
        Assert.Equal(Combo.SynthesisBegin, state.Combo);
        Assert.Equal(ActionError.None, Error(state, MuscleMemory));
        Assert.Equal(ActionError.None, Error(state, Reflect));
        Assert.Equal(ActionError.None, Error(state, TrainedEye));
    }

    [Fact]
    public void Initial_OncePerCraftActions_FollowLevelAndMask()
    {
        Assert.True(Start.TrainedPerfectionAvailable);
        Assert.True(Start.HeartAndSoulAvailable);

        var regular = CraftSimulator.Initial(P with { AllowedActions = ActionMask.Regular });
        Assert.True(regular.TrainedPerfectionAvailable);
        Assert.False(regular.HeartAndSoulAvailable);
        Assert.False(regular.QuickInnovationAvailable);

        Assert.False(CraftSimulator.Initial(P with { JobLevel = 99 }).TrainedPerfectionAvailable);
    }

    [Fact]
    public void FirstStepActions_AreGoneAfterTheFirstStep()
    {
        var state = Use(Start, BasicSynthesis);

        Assert.Equal(ActionError.ComboRequirementNotMet, Error(state, MuscleMemory));
        Assert.Equal(ActionError.ComboRequirementNotMet, Error(state, Reflect));
        Assert.Equal(ActionError.ComboRequirementNotMet, Error(state, TrainedEye));
    }

    [Fact]
    public void TrainedEye_FillsQuality()
    {
        Assert.Equal(P.MaxQuality, Use(Start, TrainedEye).Quality);
    }

    // ---- gating ----------------------------------------------------------------------------------

    [Fact]
    public void Check_LevelMaskCpAndFinal()
    {
        Assert.Equal(ActionError.InsufficientLevel, CraftSimulator.Check(P with { JobLevel = 97 }, Start, ImmaculateMend, Condition.Normal));
        Assert.Equal(ActionError.NotAllowed, CraftSimulator.Check(P with { AllowedActions = ActionMask.Without(ActionMask.All, Manipulation) }, Start, Manipulation, Condition.Normal));
        Assert.Equal(ActionError.InsufficientCp, Error(Start with { Cp = 95 }, Manipulation));
        Assert.Equal(ActionError.StateIsFinal, Error(Start with { Durability = 0 }, BasicSynthesis));
        Assert.Equal(ActionError.StateIsFinal, Error(Start with { Progress = P.MaxProgress }, BasicTouch));
    }

    [Fact]
    public void Use_WhenNotAllowed_LeavesTheStateUntouched()
    {
        var before = Start with { Cp = 3 };

        Assert.Equal(ActionError.InsufficientCp, CraftSimulator.Use(P, before, BasicTouch, Condition.Normal, true, out var after));
        Assert.Equal(before, after);
    }

    [Fact]
    public void GoodOnlyActions_NeedGoodOrExcellent()
    {
        foreach (var action in new[] { TricksOfTheTrade, PreciseTouch, IntensiveSynthesis })
        {
            Assert.Equal(ActionError.ConditionNotMet, Error(Start, action, Condition.Normal));
            Assert.Equal(ActionError.None, Error(Start, action, Condition.Good));
            Assert.Equal(ActionError.None, Error(Start, action, Condition.Excellent));
        }
    }

    [Fact]
    public void PrudentActions_AreBlockedUnderWasteNot()
    {
        var state = Use(Start, WasteNot);

        Assert.Equal(ActionError.ConditionNotMet, Error(state, PrudentTouch));
        Assert.Equal(ActionError.ConditionNotMet, Error(state, PrudentSynthesis));
    }

    [Fact]
    public void Byregots_NeedsInnerQuiet_AndSpendsIt()
    {
        Assert.Equal(ActionError.ConditionNotMet, Error(Start, ByregotsBlessing));

        var state = Use(Use(Start, BasicTouch), BasicTouch);
        Assert.Equal(2, state.InnerQuiet);

        var after = Use(state, ByregotsBlessing);
        Assert.Equal(0, after.InnerQuiet);
        Assert.Equal(state.Quality + 100 * (100 + 20 * 2) / 100 * 12 / 10, after.Quality); // potency 140, IQ2 = x1.2
    }

    [Fact]
    public void TrainedFinesse_NeedsTenInnerQuiet_AndCostsNoDurability()
    {
        Assert.Equal(ActionError.ConditionNotMet, Error(Start with { InnerQuiet = 9 }, TrainedFinesse));

        var state = Use(Start with { InnerQuiet = 10 }, TrainedFinesse);
        Assert.Equal(80, state.Durability);
        Assert.Equal(200, state.Quality); // potency 100, IQ10 = x2
    }

    // ---- costs -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(BasicTouch, 10, 5)]
    [InlineData(Groundwork, 20, 10)]
    [InlineData(PreparatoryTouch, 20, 10)]
    public void WasteNot_HalvesDurabilityCost(CraftAction action, int normal, int underWasteNot)
    {
        Assert.Equal(normal, CraftSimulator.DurabilityCost(Start, action, Condition.Normal));
        Assert.Equal(underWasteNot, CraftSimulator.DurabilityCost(Start with { WasteNot = 3 }, action, Condition.Normal));
    }

    [Fact]
    public void Sturdy_HalvesDurabilityCost_RoundingUp_AndStacksWithWasteNot()
    {
        Assert.Equal(5, CraftSimulator.DurabilityCost(Start, BasicTouch, Condition.Sturdy));
        Assert.Equal(3, CraftSimulator.DurabilityCost(Start, PrudentTouch, Condition.Sturdy));
        Assert.Equal(5, CraftSimulator.DurabilityCost(Start with { WasteNot = 2 }, Groundwork, Condition.Sturdy));
        Assert.Equal(3, CraftSimulator.DurabilityCost(Start with { WasteNot = 2 }, BasicTouch, Condition.Robust));
    }

    [Theory]
    [InlineData(PrudentTouch, 25, 13)]
    [InlineData(CarefulSynthesis, 7, 4)]
    [InlineData(Manipulation, 96, 48)]
    [InlineData(BasicSynthesis, 0, 0)]
    public void Pliant_HalvesCpCost_RoundingUp(CraftAction action, int normal, int pliant)
    {
        Assert.Equal(normal, CraftSimulator.CpCost(Start, action, Condition.Normal));
        Assert.Equal(pliant, CraftSimulator.CpCost(Start, action, Condition.Pliant));
    }

    // ---- conditions ------------------------------------------------------------------------------

    [Theory]
    [InlineData(Condition.Normal, 100)]
    [InlineData(Condition.Good, 150)]
    [InlineData(Condition.Excellent, 400)]
    [InlineData(Condition.Poor, 50)]
    [InlineData(Condition.Sturdy, 100)]
    public void Condition_ScalesQuality(Condition condition, int expected)
    {
        Assert.Equal(expected, CraftSimulator.QualityGain(P, Start, BasicTouch, condition));
    }

    [Fact]
    public void SplendorousTools_MakeGoodWorthMore()
    {
        Assert.Equal(175, CraftSimulator.QualityGain(P with { SplendorousTools = true }, Start, BasicTouch, Condition.Good));
    }

    [Fact]
    public void Malleable_ScalesProgress()
    {
        Assert.Equal(180, CraftSimulator.ProgressGain(P, Start, CarefulSynthesis, Condition.Normal));
        Assert.Equal(270, CraftSimulator.ProgressGain(P, Start, CarefulSynthesis, Condition.Malleable));
    }

    [Fact]
    public void Primed_ExtendsNewBuffsByTwo()
    {
        Assert.Equal(4, Use(Start, Innovation).Innovation);
        Assert.Equal(6, Use(Start, Innovation, Condition.Primed).Innovation);
        Assert.Equal(10, Use(Start, Manipulation, Condition.Primed).Manipulation);
        Assert.Equal(5, Use(Start, GreatStrides, Condition.Primed).GreatStrides);
    }

    // ---- actions that can fail -------------------------------------------------------------------

    [Theory]
    [InlineData(RapidSynthesis, Condition.Normal, 50)]
    [InlineData(RapidSynthesis, Condition.Centered, 75)]
    [InlineData(HastyTouch, Condition.Normal, 60)]
    [InlineData(DaringTouch, Condition.Centered, 85)]
    [InlineData(BasicTouch, Condition.Centered, 100)]
    public void SuccessPercent(CraftAction action, Condition condition, int expected)
    {
        Assert.Equal(expected, CraftSimulator.SuccessPercent(action, condition));
    }

    [Fact]
    public void FailedRapidSynthesis_CostsDurability_GainsNothing_KeepsMuscleMemory()
    {
        var state = Use(Start, MuscleMemory);
        var failed = Use(state, RapidSynthesis, success: false);

        Assert.Equal(state.Progress, failed.Progress);
        Assert.Equal(state.Durability - 10, failed.Durability);
        Assert.Equal(state.MuscleMemory - 1, failed.MuscleMemory); // ticked, not consumed

        var landed = Use(state, RapidSynthesis);
        Assert.Equal(state.Progress + 1000, landed.Progress); // 500 potency, doubled by Muscle Memory
        Assert.Equal(0, landed.MuscleMemory);
    }

    [Fact]
    public void FailedHastyTouch_KeepsGreatStrides_GivesNoInnerQuietOrExpedience()
    {
        var state = Use(Start, GreatStrides);
        var failed = Use(state, HastyTouch, success: false);

        Assert.Equal(0, failed.Quality);
        Assert.Equal(0, failed.InnerQuiet);
        Assert.Equal(2, failed.GreatStrides);
        Assert.False(failed.Expedience);
    }

    [Fact]
    public void HastyTouch_Success_BecomesDaringTouch_ForOneStep()
    {
        var state = Use(Start, HastyTouch);

        Assert.True(state.Expedience);
        Assert.Equal(ActionError.ConditionNotMet, Error(state, HastyTouch));
        Assert.Equal(ActionError.None, Error(state, DaringTouch));

        var daring = Use(state, DaringTouch);
        Assert.Equal(100 + 150 * 11 / 10, daring.Quality); // potency 150 at IQ1
        Assert.False(daring.Expedience);

        Assert.False(Use(state, Observe).Expedience); // unused, it lapses
    }

    [Fact]
    public void DaringTouch_DoesNotExistBelowLevel96()
    {
        var parameters = P with { JobLevel = 95 };
        var state = Use(parameters, CraftSimulator.Initial(parameters), HastyTouch);

        Assert.False(state.Expedience);
    }

    // ---- buffs -----------------------------------------------------------------------------------

    [Fact]
    public void Manipulation_RestoresAfterEachStep_ButNotOnTheTurnItIsCast()
    {
        var state = Use(Start with { Durability = 40 }, Manipulation);
        Assert.Equal(40, state.Durability);
        Assert.Equal(8, state.Manipulation);

        state = Use(state, BasicTouch);
        Assert.Equal(35, state.Durability); // -10, +5
        Assert.Equal(7, state.Manipulation);
    }

    [Fact]
    public void Manipulation_Recast_SkipsThatTurnsRestore()
    {
        var state = Use(Use(Start with { Durability = 40 }, Manipulation), Manipulation);

        Assert.Equal(40, state.Durability);
        Assert.Equal(8, state.Manipulation);
    }

    [Fact]
    public void Manipulation_NeverOverfills()
    {
        var state = Use(Use(Start, Manipulation), Observe);

        Assert.Equal(P.MaxDurability, state.Durability);
    }

    [Fact]
    public void GreatStridesAndInnovation_ScaleQuality()
    {
        Assert.Equal(200, CraftSimulator.QualityGain(P, Start with { GreatStrides = 1 }, BasicTouch, Condition.Normal));
        Assert.Equal(150, CraftSimulator.QualityGain(P, Start with { Innovation = 1 }, BasicTouch, Condition.Normal));
        Assert.Equal(250, CraftSimulator.QualityGain(P, Start with { GreatStrides = 1, Innovation = 1 }, BasicTouch, Condition.Normal));
    }

    [Fact]
    public void Veneration_ScalesProgress()
    {
        Assert.Equal(270, CraftSimulator.ProgressGain(P, Start with { Veneration = 1 }, CarefulSynthesis, Condition.Normal));
    }

    [Fact]
    public void Groundwork_IsHalved_WhenDurabilityCannotPayForIt()
    {
        Assert.Equal(360, CraftSimulator.ProgressGain(P, Start, Groundwork, Condition.Normal));
        Assert.Equal(180, CraftSimulator.ProgressGain(P, Start with { Durability = 10 }, Groundwork, Condition.Normal));
        Assert.Equal(360, CraftSimulator.ProgressGain(P, Start with { Durability = 10 }, Groundwork, Condition.Sturdy));
    }

    [Fact]
    public void MastersMendImmaculateMendAndTricks_RestoreUpToTheCap()
    {
        Assert.Equal(60, Use(Start with { Durability = 30 }, MasterMend).Durability);
        Assert.Equal(80, Use(Start with { Durability = 70 }, MasterMend).Durability);
        Assert.Equal(80, Use(Start with { Durability = 5 }, ImmaculateMend).Durability);

        Assert.Equal(120, Use(Start with { Cp = 100 }, TricksOfTheTrade, Condition.Good).Cp);
        Assert.Equal(600, Use(Start with { Cp = 595 }, TricksOfTheTrade, Condition.Good).Cp);
    }

    // ---- combos ----------------------------------------------------------------------------------

    [Fact]
    public void TouchCombo_DiscountsStandardThenAdvanced()
    {
        var state = Use(Start, BasicTouch);
        Assert.Equal(18, CraftSimulator.CpCost(state, StandardTouch, Condition.Normal));

        state = Use(state, StandardTouch);
        Assert.Equal(18, CraftSimulator.CpCost(state, AdvancedTouch, Condition.Normal));

        state = Use(state, AdvancedTouch);
        Assert.Equal(Combo.None, state.Combo);
        Assert.Equal(600 - 18 * 3, state.Cp);
    }

    [Fact]
    public void WithoutTheCombo_FullPrice()
    {
        Assert.Equal(32, CraftSimulator.CpCost(Start, StandardTouch, Condition.Normal));
        Assert.Equal(46, CraftSimulator.CpCost(Start, AdvancedTouch, Condition.Normal));

        // A Standard Touch that was not itself combo'd does not set up Advanced Touch.
        Assert.Equal(46, CraftSimulator.CpCost(Use(Start, StandardTouch), AdvancedTouch, Condition.Normal));
    }

    [Fact]
    public void Observe_DiscountsAdvancedTouch()
    {
        Assert.Equal(18, CraftSimulator.CpCost(Use(Start, Observe), AdvancedTouch, Condition.Normal));
    }

    [Fact]
    public void RefinedTouch_OnlyAfterBasicTouch_AndGivesTwoInnerQuiet()
    {
        Assert.Equal(ActionError.ComboRequirementNotMet, Error(Start, RefinedTouch));

        var state = Use(Use(Start, BasicTouch), RefinedTouch);
        Assert.Equal(3, state.InnerQuiet);
    }

    [Theory]
    [InlineData(PreciseTouch, Condition.Good)]
    [InlineData(PreparatoryTouch, Condition.Normal)]
    [InlineData(Reflect, Condition.Normal)]
    public void DoubleInnerQuietActions(CraftAction action, Condition condition)
    {
        Assert.Equal(2, Use(Start, action, condition).InnerQuiet);
    }

    [Fact]
    public void MuscleMemory_LastsFiveSteps_AndIsSpentByTheNextSynthesis()
    {
        var state = Use(Start, MuscleMemory);
        Assert.Equal(5, state.MuscleMemory);
        Assert.Equal(300, state.Progress);

        state = Use(state, Observe);
        Assert.Equal(4, state.MuscleMemory);

        state = Use(state, CarefulSynthesis);
        Assert.Equal(0, state.MuscleMemory);
        Assert.Equal(300 + 360, state.Progress);
    }

    // ---- once-per-craft and specialist actions ---------------------------------------------------

    [Fact]
    public void TrainedPerfection_MakesTheNextDurabilityActionFree_Once()
    {
        var state = Use(Start, TrainedPerfection);
        Assert.True(state.TrainedPerfectionActive);
        Assert.Equal(ActionError.NoRemainingUses, Error(state, TrainedPerfection));

        state = Use(state, Innovation); // a buff does not spend it
        Assert.True(state.TrainedPerfectionActive);

        state = Use(state, Groundwork);
        Assert.Equal(80, state.Durability);
        Assert.False(state.TrainedPerfectionActive);

        Assert.Equal(60, Use(state, Groundwork).Durability);
    }

    [Fact]
    public void HeartAndSoul_IsInstant_AndLetsAGoodOnlyActionThrough_Once()
    {
        var buffed = Use(Start, Innovation);
        var state = Use(buffed, HeartAndSoul);

        Assert.True(state.HeartAndSoulActive);
        Assert.Equal(buffed.Innovation, state.Innovation); // no step spent, nothing ticked
        Assert.Equal(ActionError.NoRemainingUses, Error(state, HeartAndSoul));

        state = Use(state, PreciseTouch);
        Assert.False(state.HeartAndSoulActive);
        Assert.Equal(ActionError.ConditionNotMet, Error(state, PreciseTouch));
    }

    [Fact]
    public void HeartAndSoul_IsNotSpent_WhenTheConditionWasGoodAnyway()
    {
        var state = Use(Use(Start, HeartAndSoul), PreciseTouch, Condition.Good);

        Assert.True(state.HeartAndSoulActive);
    }

    [Fact]
    public void HeartAndSoul_AsTheVeryFirstAction_KeepsFirstStepActionsUsable()
    {
        var state = Use(Start, HeartAndSoul);

        Assert.Equal(ActionError.None, Error(state, Reflect));
    }

    [Fact]
    public void QuickInnovation_IsInstant_OneTurn_Once_AndNotOverInnovation()
    {
        Assert.Equal(ActionError.ConditionNotMet, Error(Use(Start, Innovation), QuickInnovation));

        var state = Use(Start, QuickInnovation);
        Assert.Equal(1, state.Innovation);
        Assert.Equal(ActionError.NoRemainingUses, Error(state, QuickInnovation));

        state = Use(state, BasicTouch);
        Assert.Equal(150, state.Quality);
        Assert.Equal(0, state.Innovation);
    }

    // ---- ending ----------------------------------------------------------------------------------

    [Fact]
    public void ReachingFullProgress_IsSuccess_AndProgressIsCapped()
    {
        var state = Use(Start with { Progress = P.MaxProgress - 10 }, CarefulSynthesis);

        Assert.Equal(P.MaxProgress, state.Progress);
        Assert.True(CraftSimulator.IsFinal(P, state));
        Assert.True(CraftSimulator.IsSuccess(P, state));
    }

    [Fact]
    public void RunningOutOfDurability_IsFailure()
    {
        var state = Use(Start with { Durability = 10 }, BasicTouch);

        Assert.Equal(0, state.Durability);
        Assert.True(CraftSimulator.IsFinal(P, state));
        Assert.False(CraftSimulator.IsSuccess(P, state));
    }

    [Fact]
    public void Quality_IsCappedAtTheRecipeMaximum()
    {
        var state = Use(Start with { Quality = P.MaxQuality - 5 }, BasicTouch);

        Assert.Equal(P.MaxQuality, state.Quality);
    }

    /// <summary>The lookahead relies on this: every action is either usable or reports why not — never throws.</summary>
    [Fact]
    public void EveryAction_FromEveryReachableKindOfState_EitherWorksOrExplains()
    {
        var states = new[] { Start, Use(Start, BasicTouch), Use(Start, WasteNot), Start with { Cp = 0 }, Start with { InnerQuiet = 10 } };

        foreach (var state in states)
        foreach (var action in Enum.GetValues<CraftAction>())
        foreach (var condition in Enum.GetValues<Condition>())
        {
            var error = CraftSimulator.Use(P, state, action, condition, true, out var next);
            if (error != ActionError.None)
                Assert.Equal(state, next);
            else
                Assert.InRange(next.Cp, 0, P.MaxCp);
        }
    }
}
