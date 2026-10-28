using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;
using static Hephaestus.Tests.Features.Crafting.Solver.SolverFixtures;

namespace Hephaestus.Tests.Features.Crafting.Solver;

public class LiveEvaluatorTests
{
    // No time budget in tests: a wall-clock cutoff would make the answer depend on the machine.
    private static readonly EvaluatorSettings Depth2 = new() { MaxDepth = 2, Budget = TimeSpan.MaxValue };

    /// <summary>An evaluator that has watched the plan's first <paramref name="steps"/> steps happen, and the state they lead to.</summary>
    private static (LiveEvaluator Evaluator, CraftState State) AtPlanStep(CraftParameters parameters, int steps, EvaluatorSettings? settings = null)
    {
        var evaluator = new LiveEvaluator(parameters, Baseline, NormalRecipe, settings ?? Depth2);
        return (evaluator, AfterPlanSteps(parameters, steps, evaluator.Observe));
    }

    [Fact]
    public void UnderNormal_WithNothingBetterOnOffer_ItFollowsTheBaseline()
    {
        var (evaluator, state) = AtPlanStep(FullStats, 0);

        var recommendation = evaluator.Recommend(state, Condition.Normal);

        Assert.Equal(MuscleMemory, recommendation.Action);
        Assert.True(recommendation.FollowsBaseline);
        Assert.Equal(BaselineStatus.Alive, recommendation.Baseline);
        Assert.Equal(new[] { Manipulation, Veneration, WasteNot2 }, recommendation.Preview);
        Assert.Equal(2, recommendation.Depth);
    }

    /// <summary>The headline behaviour: an unplanned Good where a touch was coming up is worth more as a Precise Touch.</summary>
    [Fact]
    public void OnAGood_WhereBasicTouchWasPlanned_ItTakesPreciseTouch()
    {
        var (evaluator, state) = AtPlanStep(WeakStats, 7);

        var recommendation = evaluator.Recommend(state, Condition.Good);

        Assert.Equal(PreciseTouch, recommendation.Action);
        Assert.False(recommendation.FollowsBaseline);
        Assert.Contains("instead of BasicTouch", recommendation.Reason);
    }

    [Fact]
    public void OnAnExcellent_ItSpendsItOnAtLeastAsBigATouchAsPlanned()
    {
        var (evaluator, state) = AtPlanStep(WeakStats, 7);

        var action = evaluator.Recommend(state, Condition.Excellent).Action!.Value;

        Assert.True(
            CraftSimulator.QualityGain(WeakStats, state, action, Condition.Excellent)
            > CraftSimulator.QualityGain(WeakStats, state, BasicTouch, Condition.Excellent),
            $"{action} should out-earn the planned Basic Touch on an Excellent");
    }

    /// <summary>A Poor halves a touch. Better to do something the plan needed anyway and touch next turn.</summary>
    [Fact]
    public void OnAPoor_WhereATouchWasPlanned_ItDoesNotWasteTheTouch()
    {
        var (evaluator, state) = AtPlanStep(WeakStats, 7);

        var action = evaluator.Recommend(state, Condition.Poor).Action!.Value;

        Assert.Equal(0, CraftSimulator.QualityGain(WeakStats, state, action, Condition.Poor));
    }

    [Fact]
    public void OnAGood_WhereGroundworkWasPlanned_ItTakesIntensiveSynthesis()
    {
        var (evaluator, state) = AtPlanStep(WeakStats, 4);

        Assert.Equal(IntensiveSynthesis, evaluator.Recommend(state, Condition.Good).Action);
    }

    /// <summary>Reality far behind the plan: the plan is dropped and the item is saved first.</summary>
    [Fact]
    public void InADurabilityEmergency_ItDropsTheBaselineAndMends()
    {
        var evaluator = new LiveEvaluator(FullStats, Baseline, NormalRecipe, Depth2);
        var state = CraftSimulator.Initial(FullStats) with { Durability = 10, Cp = 150, Progress = 3000 };

        var recommendation = evaluator.Recommend(state, Condition.Normal);

        Assert.Equal(MasterMend, recommendation.Action);
        Assert.NotEqual(BaselineStatus.Alive, recommendation.Baseline);
        Assert.Contains("baseline dropped", recommendation.Reason);
    }

    /// <summary>
    /// The safety margin at work. One Rapid Synthesis (50%) would finish this craft right now, and
    /// failing it breaks the item; mending first and finishing safely costs 88 CP and three turns.
    /// Averaged over the dice the gamble is worth half a craft — so it mends.
    /// </summary>
    [Fact]
    public void ItWillNotGambleTheItem_WhenASafeFinishExists()
    {
        var evaluator = new LiveEvaluator(FullStats, Array.Empty<CraftAction>(), NormalRecipe, Depth2);
        var state = CraftSimulator.Initial(FullStats) with { Progress = FullStats.MaxProgress - 1190, Durability = 10, Cp = 200 };

        var action = evaluator.Recommend(state, Condition.Normal).Action;

        Assert.Equal(MasterMend, action);
    }

    [Fact]
    public void ButItWillGamble_WhenTheGambleIsTheOnlyWayToFinish()
    {
        var evaluator = new LiveEvaluator(FullStats, Array.Empty<CraftAction>(), NormalRecipe, Depth2);
        var state = CraftSimulator.Initial(FullStats) with { Progress = FullStats.MaxProgress - 1190, Durability = 10, Cp = 0 };

        Assert.Equal(RapidSynthesis, evaluator.Recommend(state, Condition.Normal).Action);
    }

    /// <summary>
    /// Benchmark seed 61 on raphael's other Black Star plan. Hasty Touch here would break the plan's
    /// Basic → Standard → Advanced pricing and leave it a few CP short of its last synthesis; two
    /// plies on, the search could repair that — but before then the tracker's hard rule drops the
    /// plan, and with no plan to close out the leaves the craft ended 800 quality short. The
    /// search now applies the same rule at its own decision nodes, so it keeps the combo.
    /// </summary>
    [Fact]
    public void ItDoesNotCountOnARepairTheHardRuleWouldNotAllow()
    {
        var evaluator = new LiveEvaluator(FullStats, BenchmarkRecipes.BlackStarUncappedPlan, NormalRecipe, Depth2);
        var state = CraftSimulator.Initial(FullStats);
        foreach (var (action, condition) in new[]
                 {
                     (Reflect, Condition.Normal), (Innovation, Condition.Normal), (PrudentTouch, Condition.Normal),
                     (PrudentTouch, Condition.Normal), (PreciseTouch, Condition.Good), (Manipulation, Condition.Normal),
                     (HastyTouch, Condition.Good), (Innovation, Condition.Normal), (WasteNot2, Condition.Good),
                 })
        {
            Assert.Equal(ActionError.None, CraftSimulator.Use(FullStats, state, action, condition, true, out state));
            evaluator.Observe(action);
        }

        var recommendation = evaluator.Recommend(state, Condition.Normal);

        Assert.Equal(BasicTouch, recommendation.Action);
        Assert.Equal(BaselineStatus.Alive, recommendation.Baseline);
    }

    [Fact]
    public void OnceTheQualityGoalIsMet_ItStopsSpendingOnQuality()
    {
        var settings = Depth2 with { QualityGoal = 3000 };
        var (evaluator, state) = AtPlanStep(FullStats, 7, settings);
        state.Quality = 3000;

        var action = evaluator.Recommend(state, Condition.Good).Action!.Value;

        Assert.Equal(0, CraftSimulator.QualityGain(FullStats, state, action, Condition.Good));
    }

    [Fact]
    public void WithNoBaselineAtAll_ItStillCraftsSensibly()
    {
        var summary = PolicyPlayer.Sample(FullStats, Array.Empty<CraftAction>(), NormalRecipe, runs: 20, seed: 5,
            new EvaluatorSettings { MaxDepth = 2, Budget = TimeSpan.MaxValue });

        Assert.Equal(1.0, summary.CompletionRate);
        Assert.True(summary.MeanQuality > 0, "with no plan it should still find quality on its own");
    }

    [Fact]
    public void WhenTheSynthesisIsOver_ThereIsNothingToRecommend()
    {
        var evaluator = new LiveEvaluator(FullStats, Baseline, NormalRecipe, Depth2);
        var finished = CraftSimulator.Initial(FullStats) with { Progress = FullStats.MaxProgress };

        var recommendation = evaluator.Recommend(finished, Condition.Normal);

        Assert.Null(recommendation.Action);
        Assert.Empty(recommendation.Preview);
    }

    /// <summary>A starved budget must still answer — with whatever depth it finished, never with nothing.</summary>
    [Fact]
    public void ATinyBudget_StillAnswers()
    {
        var settings = new EvaluatorSettings { MaxDepth = 6, Budget = TimeSpan.FromMilliseconds(1) };
        var (evaluator, state) = AtPlanStep(FullStats, 7, settings);

        var recommendation = evaluator.Recommend(state, Condition.Normal);

        Assert.NotNull(recommendation.Action);
        Assert.InRange(recommendation.Depth, 1, 6);
        Assert.Equal(ActionError.None, CraftSimulator.Check(FullStats, state, recommendation.Action!.Value, Condition.Normal));
    }

    [Fact]
    public void SameInputs_SameAnswer()
    {
        var (first, state) = AtPlanStep(WeakStats, 9);
        var (second, _) = AtPlanStep(WeakStats, 9);

        Assert.Equal(first.Recommend(state, Condition.Good).Action, second.Recommend(state, Condition.Good).Action);
    }
}
