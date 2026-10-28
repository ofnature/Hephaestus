using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;
using static Hephaestus.Tests.Features.Crafting.Solver.SolverFixtures;

namespace Hephaestus.Tests.Features.Crafting.Solver;

public class BaselineTrackerTests
{
    private static BaselineTracker Tracker(CraftParameters? parameters = null, float deviation = 25f)
        => new(parameters ?? FullStats, Baseline, deviation);

    [Fact]
    public void FollowingThePlan_ConsumesItInOrder()
    {
        var tracker = Tracker();
        Assert.Equal(MuscleMemory, tracker.Next);

        tracker.Observe(MuscleMemory);
        tracker.Observe(Manipulation);

        Assert.Equal(Veneration, tracker.Next);
        Assert.Equal(Baseline.Length - 2, tracker.Remaining.Count);
    }

    /// <summary>Tricks of the Trade on a Good is an extra the plan never had — the plan just resumes after it.</summary>
    [Fact]
    public void AnInsertion_ConsumesNothing()
    {
        var tracker = Tracker();
        tracker.Observe(MuscleMemory);

        tracker.Observe(TricksOfTheTrade);
        tracker.Observe(Observe);

        Assert.Equal(Manipulation, tracker.Next);
    }

    /// <summary>Precise Touch where Basic Touch was planned takes that step's place.</summary>
    [Fact]
    public void ASameKindAction_ReplacesTheNextStep()
    {
        var tracker = Tracker();
        for (var i = 0; i < 7; i++)
            tracker.Observe(Baseline[i]);
        Assert.Equal(BasicTouch, tracker.Next);

        tracker.Observe(PreciseTouch);

        Assert.Equal(RefinedTouch, tracker.Next);
    }

    [Fact]
    public void ASynthesis_DoesNotReplaceATouch()
    {
        var tracker = Tracker();
        for (var i = 0; i < 7; i++)
            tracker.Observe(Baseline[i]);

        tracker.Observe(CarefulSynthesis);

        Assert.Equal(BasicTouch, tracker.Next);
    }

    /// <summary>Doing the planned Innovation early (to sit out a Poor) removes THAT step, not the next one.</summary>
    [Fact]
    public void AStepPlannedALittleLater_CanBePulledForward()
    {
        var tracker = Tracker();
        for (var i = 0; i < 8; i++)
            tracker.Observe(Baseline[i]);
        Assert.Equal(RefinedTouch, tracker.Next); // then DelicateSynthesis, then Innovation

        tracker.Observe(Innovation);

        Assert.Equal(RefinedTouch, tracker.Next);
        Assert.Equal(new[] { RefinedTouch, DelicateSynthesis, PreparatoryTouch }, tracker.Remaining.Take(3));
    }

    [Fact]
    public void PullForward_OnlyReachesAFewStepsAhead()
    {
        var tracker = Tracker();

        tracker.Observe(ByregotsBlessing); // planned 19 steps away — far outside the window

        Assert.Equal(Baseline.Length, tracker.Remaining.Count);
    }

    /// <summary>The plan was solved for other stats: steps it cannot afford itself are not part of it.</summary>
    [Fact]
    public void StepsThePlanCannotUse_AreDroppedUpFront()
    {
        Assert.Equal(Baseline.Length, Tracker(FullStats).Remaining.Count);
        Assert.Equal(Baseline.Length - 2, Tracker(WeakStats).Remaining.Count);
    }

    [Fact]
    public void Assess_OnPlan_StaysAlive()
    {
        var tracker = Tracker();
        var state = AfterPlanSteps(FullStats, 6, tracker.Observe);

        Assert.Equal(BaselineStatus.Alive, tracker.Assess(state));
    }

    /// <summary>Hard rule: the remaining steps, from where we really are, would break the item.</summary>
    [Fact]
    public void Assess_WhenTheRestOfThePlanWouldBreakTheItem_DropsIt()
    {
        var tracker = Tracker(deviation: 100f); // soft rule out of the way
        var state = AfterPlanSteps(FullStats, 6, tracker.Observe) with { Durability = 10, Manipulation = 0 };

        Assert.Equal(BaselineStatus.CannotComplete, tracker.Assess(state));
        Assert.Empty(tracker.Remaining);
        Assert.Null(tracker.Next);
    }

    /// <summary>
    /// The hard rule closes the plan out with the finisher, as the lookahead's leaves do: a plan a
    /// few CP short of its final Careful Synthesis still finishes on a free Basic Synthesis.
    /// </summary>
    [Fact]
    public void Assess_APlanTheFinisherCanStillCloseOut_StaysAlive()
    {
        var plan = BenchmarkRecipes.BlackStarUncappedPlan;
        var tracker = new BaselineTracker(FullStats, plan, 100f); // soft rule out of the way
        var state = CraftSimulator.Initial(FullStats);
        foreach (var action in plan[..^1])
        {
            Assert.Equal(ActionError.None, CraftSimulator.Use(FullStats, state, action, Condition.Normal, true, out state));
            tracker.Observe(action);
        }

        Assert.Equal(CarefulSynthesis, tracker.Next);
        var shortOfCp = state with { Cp = 5, Progress = FullStats.MaxProgress - 400 }; // Careful costs 7

        Assert.Equal(BaselineStatus.Alive, tracker.Assess(shortOfCp));
    }

    /// <summary>Soft rule: far enough behind the plan's own numbers.</summary>
    [Fact]
    public void Assess_FarBehindOnCp_DropsItAsDrifted()
    {
        var tracker = Tracker();
        var state = AfterPlanSteps(FullStats, 6, tracker.Observe);

        Assert.Equal(BaselineStatus.Drifted, tracker.Assess(state with { Cp = state.Cp - 200 }));
    }

    /// <summary>
    /// Precise Touch on a Good replaces the planned Basic Touch, which leaves the Refined Touch after
    /// it unusable at the front of the plan for good. Following the rest of the plan from there is
    /// not drift — the yardstick is the plan after the steps actually consumed, not the plan's state
    /// before a step that will never come.
    /// </summary>
    [Fact]
    public void Assess_AStuckFrontStep_DoesNotFreezeTheYardstick()
    {
        var tracker = Tracker();
        var state = AfterPlanSteps(FullStats, 7, tracker.Observe);
        foreach (var (action, condition) in new[]
                 {
                     (PreciseTouch, Condition.Good), (DelicateSynthesis, Condition.Normal), (Innovation, Condition.Normal),
                     (PreparatoryTouch, Condition.Normal), (BasicTouch, Condition.Normal), (StandardTouch, Condition.Normal),
                     (AdvancedTouch, Condition.Normal),
                 })
        {
            Assert.Equal(ActionError.None, CraftSimulator.Use(FullStats, state, action, condition, true, out state));
            tracker.Observe(action);
        }

        Assert.Equal(RefinedTouch, tracker.Next);
        Assert.Equal(BaselineStatus.Alive, tracker.Assess(state));
    }

    [Fact]
    public void BeingAheadOfThePlan_IsNotDrift()
    {
        var tracker = Tracker();
        var planned = AfterPlanSteps(FullStats, 6);

        Assert.Equal(0, tracker.AdverseDriftPercent(planned with { Cp = planned.Cp + 200, Progress = planned.Progress + 900 }, planned));
        Assert.True(tracker.AdverseDriftPercent(planned with { Durability = planned.Durability - 40 }, planned) >= 49);
    }

    [Fact]
    public void OnceDropped_ItStaysDropped()
    {
        var tracker = Tracker();
        var state = AfterPlanSteps(FullStats, 6, tracker.Observe);
        tracker.Assess(state with { Cp = 0 });

        Assert.NotEqual(BaselineStatus.Alive, tracker.Assess(state));
    }

    [Fact]
    public void Replay_OfTheWholePlan_MatchesPlayingIt()
    {
        var replayed = BaselineTracker.Replay(FullStats, CraftSimulator.Initial(FullStats), Baseline, 0UL, int.MaxValue, out var steps);

        Assert.Equal(RotationPlayer.PlayNormal(FullStats, Baseline).Final, replayed);
        Assert.Equal(Baseline.Length, steps);
    }

    /// <summary>With the goal already met there is no reason to spend durability and CP on more quality.</summary>
    [Fact]
    public void Replay_SkipsQualityActions_OnceTheGoalIsMet()
    {
        var full = RotationPlayer.PlayNormal(FullStats, Baseline).Final;
        var withGoal = BaselineTracker.Replay(FullStats, CraftSimulator.Initial(FullStats), Baseline, 0UL, qualityGoal: 0, out var steps);

        // Delicate Synthesis still runs (it is progress too), so some quality arrives regardless.
        Assert.True(withGoal.Quality < full.Quality / 4);
        Assert.True(steps < Baseline.Length);
        Assert.True(withGoal.Cp > full.Cp);
    }

    [Fact]
    public void Replay_SkipsConsumedSteps()
    {
        var all = BaselineTracker.Replay(FullStats, CraftSimulator.Initial(FullStats), Baseline, 0UL, int.MaxValue, out _);
        var withoutByregots = BaselineTracker.Replay(FullStats, CraftSimulator.Initial(FullStats), Baseline, 1UL << 19, int.MaxValue, out _);

        Assert.True(withoutByregots.Quality < all.Quality);
    }
}
