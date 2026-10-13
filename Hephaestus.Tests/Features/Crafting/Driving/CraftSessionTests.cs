using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using Hephaestus.Tests.Features.Crafting.Solver;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Tests.Features.Crafting.Driving;

public class CraftSessionTests
{
    private static readonly CraftParameters P = SolverFixtures.FullStats;

    private static readonly BaselineRequest Request = new(650, 6300, 11400, 80, false, 94, 238, 300, 3957, 3896, 601, 0, true, false, false, false);

    private static readonly EvaluatorSettings Settings = new() { MaxDepth = 1, Budget = TimeSpan.MaxValue };

    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Runs evaluations inline, so a test sees the answer on the very next tick.</summary>
    private static Task<Recommendation> Inline(Func<Recommendation> work) => Task.FromResult(work());

    private static CraftSession Session(
        Func<BaselineRequest, Task<IReadOnlyList<CraftAction>?>>? solve = null, bool solverAvailable = true)
    {
        solve ??= _ => Task.FromResult<IReadOnlyList<CraftAction>?>(SolverFixtures.Baseline);
        return new CraftSession(_ => new CraftSetup(P, SolverFixtures.NormalRecipe, Request), solverAvailable ? solve : null, () => Settings, Inline);
    }

    /// <summary>The window as it looks at step 1 of a fresh craft.</summary>
    private static SynthesisSnapshot Fresh() => Snapshot(CraftSimulator.Initial(P), stepIndex: 1);

    private static SynthesisSnapshot Snapshot(CraftState state, int stepIndex, Condition condition = Condition.Normal) => new(
        RecipeId: 35000, StepIndex: stepIndex, Progress: state.Progress, MaxProgress: P.MaxProgress, Quality: state.Quality,
        MaxQuality: P.MaxQuality, Durability: state.Durability, MaxDurability: P.MaxDurability, Condition: condition,
        Cp: state.Cp, MaxCp: P.MaxCp, InnerQuiet: state.InnerQuiet, WasteNot: state.WasteNot, Innovation: state.Innovation,
        Veneration: state.Veneration, GreatStrides: state.GreatStrides, MuscleMemory: state.MuscleMemory,
        Manipulation: state.Manipulation, TrainedPerfectionActive: state.TrainedPerfectionActive,
        HeartAndSoulActive: state.HeartAndSoulActive, Expedience: state.Expedience);

    private CraftAction? Tick(CraftSession session, SynthesisSnapshot? snapshot, bool auto = true, bool transitioning = false, double advanceSeconds = 0.5)
    {
        _now = _now.AddSeconds(advanceSeconds);
        return session.Tick(snapshot, transitioning, auto, _now);
    }

    /// <summary>Tick the same snapshot until the session presses something (settle, evaluate, press take a few ticks).</summary>
    private CraftAction? TickUntilPress(CraftSession session, SynthesisSnapshot snapshot, bool auto = true)
    {
        for (var i = 0; i < 6; i++)
        {
            if (Tick(session, snapshot, auto) is { } pressed)
                return pressed;
        }

        return null;
    }

    [Fact]
    public void WithNoSynthesisOpen_ItIsIdle_AndSaysSo()
    {
        var session = Session();

        Assert.Null(Tick(session, null));
        Assert.False(session.Active);
        Assert.Contains("Idle", session.Status);
    }

    [Fact]
    public void Auto_PressesTheBaselinesFirstStep()
    {
        var session = Session();

        Assert.Equal(MuscleMemory, TickUntilPress(session, Fresh()));
        Assert.True(session.Current!.Value.FollowsBaseline);
    }

    [Fact]
    public void Assisted_RecommendsButNeverPresses()
    {
        var session = Session();

        Assert.Null(TickUntilPress(session, Fresh(), auto: false));
        Assert.Equal(MuscleMemory, session.Current!.Value.Action);
    }

    /// <summary>Progress, CP and buffs arrive a few frames apart — a state that just changed is not trusted yet.</summary>
    [Fact]
    public void AStateThatJustChanged_IsNotActedOn_UntilItSettles()
    {
        var session = Session();

        Assert.Null(Tick(session, Fresh(), advanceSeconds: 0.05));
        Assert.Null(Tick(session, Fresh(), advanceSeconds: 0.05));
        Assert.Null(session.Current);
    }

    [Fact]
    public void WhileAnActionIsResolving_ItDoesNothing()
    {
        var session = Session();
        TickUntilPress(session, Fresh());

        Assert.Null(Tick(session, Fresh(), transitioning: true, advanceSeconds: 5));
        Assert.Contains("resolve", session.Status);
    }

    [Fact]
    public void ItWaitsForRaphael_BeforeTheFirstAction()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<CraftAction>?>();
        var session = Session(_ => pending.Task);

        Assert.Null(TickUntilPress(session, Fresh()));
        Assert.Contains("Raphael", session.Status);

        pending.SetResult(SolverFixtures.Baseline);
        Assert.Equal(MuscleMemory, TickUntilPress(session, Fresh()));
    }

    [Fact]
    public void IfRaphaelNeverAnswers_ItCarriesOnWithoutABaseline()
    {
        var never = new TaskCompletionSource<IReadOnlyList<CraftAction>?>();
        var session = Session(_ => never.Task);
        Tick(session, Fresh());

        Tick(session, Fresh(), advanceSeconds: CraftSession.SolveTimeout.TotalSeconds + 1);
        var pressed = TickUntilPress(session, Fresh());

        Assert.NotNull(pressed);
        Assert.False(session.Current!.Value.FollowsBaseline);
    }

    [Theory]
    [InlineData(true)]  // the solver answered "no solution"
    [InlineData(false)] // raphael-cli is not installed at all
    public void WithNoBaselineToBeHad_ItStillCrafts(bool solverAvailable)
    {
        var session = Session(_ => Task.FromResult<IReadOnlyList<CraftAction>?>(null), solverAvailable);

        Assert.NotNull(TickUntilPress(session, Fresh()));
    }

    [Fact]
    public void AFailedSolve_IsNotFatal()
    {
        var session = Session(_ => Task.FromException<IReadOnlyList<CraftAction>?>(new InvalidOperationException("raphael crashed")));

        Assert.NotNull(TickUntilPress(session, Fresh()));
    }

    [Fact]
    public void APress_IsRepeatedOnlyAfterThePacingGap()
    {
        var session = Session();
        Assert.Equal(MuscleMemory, TickUntilPress(session, Fresh()));

        Assert.Null(Tick(session, Fresh(), advanceSeconds: 0.2));
        Assert.Equal(MuscleMemory, Tick(session, Fresh(), advanceSeconds: CraftSession.PressPacing.TotalSeconds));
    }

    /// <summary>The game is the authority. An action it keeps refusing is ruled out, and something else is chosen.</summary>
    [Fact]
    public void AnActionTheGameKeepsRefusing_IsMasked_AndAnotherIsChosen()
    {
        var session = Session();
        var presses = new List<CraftAction>();

        for (var i = 0; i < 12; i++)
        {
            if (Tick(session, Fresh(), advanceSeconds: CraftSession.PressPacing.TotalSeconds) is { } pressed)
                presses.Add(pressed);
        }

        Assert.Equal(CraftSession.MaxPresses, presses.Count(a => a == MuscleMemory));
        Assert.Contains(presses, a => a != MuscleMemory);
    }

    [Fact]
    public void WhenTheStateMovesOn_ItThinksAgain_AndFollowsThePlanOnward()
    {
        var session = Session();
        Assert.Equal(MuscleMemory, TickUntilPress(session, Fresh()));

        session.OnActionUsed(MuscleMemory);
        CraftSimulator.Use(P, CraftSimulator.Initial(P), MuscleMemory, Condition.Normal, true, out var after);

        Assert.Equal(Manipulation, TickUntilPress(session, Snapshot(after, stepIndex: 2)));
    }

    /// <summary>In Assisted mode the player may ignore the advice; the plan has to absorb what they actually did.</summary>
    [Fact]
    public void ThePlayersOwnAction_IsFedToTheEvaluator()
    {
        var session = Session();
        TickUntilPress(session, Fresh(), auto: false);

        // They opened with Reflect instead of the planned Muscle Memory.
        session.OnActionUsed(Reflect);
        CraftSimulator.Use(P, CraftSimulator.Initial(P), Reflect, Condition.Normal, true, out var after);
        TickUntilPress(session, Snapshot(after, stepIndex: 2), auto: false);

        Assert.NotNull(session.Current!.Value.Action);
        Assert.NotEqual(MuscleMemory, session.Current.Value.Action); // first-step only — no longer usable
    }

    [Fact]
    public void TheTouchCombo_IsRememberedFromTheActionsSeen()
    {
        var session = Session();
        Tick(session, Fresh());

        session.OnActionUsed(BasicTouch);
        Assert.Equal(Combo.BasicTouch, session.ToState(Fresh()).Combo);

        session.OnActionUsed(StandardTouch);
        Assert.Equal(Combo.StandardTouch, session.ToState(Fresh()).Combo);

        session.OnActionUsed(Innovation);
        Assert.Equal(Combo.None, session.ToState(Fresh()).Combo);

        session.OnActionUsed(Observe);
        Assert.Equal(Combo.StandardTouch, session.ToState(Fresh()).Combo);
    }

    [Fact]
    public void BeforeAnyStep_FirstStepActionsAreAvailable_AndAnInstantActionDoesNotSpendThat()
    {
        var session = Session();
        Tick(session, Fresh());
        Assert.Equal(Combo.SynthesisBegin, session.ToState(Fresh()).Combo);

        session.OnActionUsed(HeartAndSoul);
        Assert.Equal(Combo.SynthesisBegin, session.ToState(Fresh()).Combo);

        session.OnActionUsed(BasicSynthesis);
        Assert.NotEqual(Combo.SynthesisBegin, session.ToState(Fresh()).Combo);
    }

    [Fact]
    public void OncePerCraftActions_AreRememberedAsSpent()
    {
        var level100 = P with { JobLevel = 100, AllowedActions = ActionMask.All };
        var session = new CraftSession(_ => new CraftSetup(level100, SolverFixtures.NormalRecipe, Request), null, () => Settings, Inline);
        Tick(session, Fresh());
        Assert.True(session.ToState(Fresh()).TrainedPerfectionAvailable);

        session.OnActionUsed(TrainedPerfection);

        Assert.False(session.ToState(Fresh()).TrainedPerfectionAvailable);
    }

    /// <summary>Reloading the plugin mid-craft: there is no baseline to be had, and the first-step actions are gone.</summary>
    [Fact]
    public void JoiningACraftAlreadyUnderWay_AsksForNoBaseline_AndKnowsTheFirstStepIsOver()
    {
        var solves = 0;
        var session = Session(_ => { solves++; return Task.FromResult<IReadOnlyList<CraftAction>?>(SolverFixtures.Baseline); });
        var midway = Snapshot(SolverFixtures.AfterPlanSteps(P, 5), stepIndex: 6);

        var pressed = TickUntilPress(session, midway);

        Assert.Equal(0, solves);
        Assert.NotNull(pressed);
        Assert.NotEqual(Combo.SynthesisBegin, session.ToState(midway).Combo);
    }

    [Fact]
    public void WhenTheWindowCloses_TheSessionEnds_AndTheNextCraftStartsClean()
    {
        var solves = 0;
        var session = Session(_ => { solves++; return Task.FromResult<IReadOnlyList<CraftAction>?>(SolverFixtures.Baseline); });
        TickUntilPress(session, Fresh());
        session.OnActionUsed(MuscleMemory);

        Tick(session, null);
        Assert.False(session.Active);
        Assert.Null(session.Current);

        Assert.Equal(MuscleMemory, TickUntilPress(session, Fresh()));
        Assert.Equal(2, solves);
        Assert.Equal(Combo.SynthesisBegin, session.ToState(Fresh()).Combo);
    }

    [Fact]
    public void UntilTheRecipeDataIsReady_NothingHappens()
    {
        var session = new CraftSession(_ => null, null, () => Settings, Inline);

        Assert.Null(TickUntilPress(session, Fresh()));
        Assert.False(session.Active);
    }

    /// <summary>End to end: drive a whole craft through the session against the simulator, Normal all the way.</summary>
    [Fact]
    public void AWholeCraft_DrivenThroughTheSession_Completes()
    {
        var session = Session();
        var state = CraftSimulator.Initial(P);
        var step = 1;

        for (var guard = 0; guard < 80 && !CraftSimulator.IsFinal(P, state); guard++)
        {
            if (TickUntilPress(session, Snapshot(state, step)) is not { } action)
                break;

            Assert.Equal(ActionError.None, CraftSimulator.Use(P, state, action, Condition.Normal, true, out var next));
            session.OnActionUsed(action);
            state = next;
            if (CraftSimulator.SpendsStep(action))
                step++;
        }

        Assert.True(CraftSimulator.IsSuccess(P, state));
        Assert.Equal(P.MaxQuality, state.Quality); // the plan caps quality under Normal; following it must too
    }
}
