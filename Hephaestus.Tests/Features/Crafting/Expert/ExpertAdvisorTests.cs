using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Tests.Sandbox.Expert;

namespace Hephaestus.Tests.Features.Crafting.Expert;

/// <summary>
/// The expert solver as the plugin seats it at a craft. How GOOD it is belongs to the sandbox's
/// benchmarks; here: it only ever names actions the game would take, it respects what the game has
/// refused, and one craft never takes more of a mission's duty action than it was allowed.
/// </summary>
public class ExpertAdvisorTests
{
    private static readonly SearchSettings Search = new(Samples: 32);

    [Fact]
    public void EveryAnswer_SaysAboutHowLongTheItemStillTakes_AndTheFigureShrinksAsTheCraftGoesOn()
    {
        var problem = MasterMission.All[0].Problem;
        var advisor = new ExpertAdvisor(problem, () => ExpertTuning.Default, null, TimeSpan.Zero);
        var parameters = problem.Parameters;
        var random = new Random(3);
        var odds = ConditionOdds.ForRecipe(problem.ConditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(problem.ConditionsFlag);
        var state = ExpertPlayer.Start(problem, 0);
        var condition = Condition.Normal;
        var estimates = new List<double>();

        for (var i = 0; i < 200 && !CraftSimulator.IsFinal(parameters, state); i++)
        {
            var recommendation = advisor.Recommend(state, condition);
            var action = recommendation.Action!.Value;
            Assert.True(recommendation.SecondsLeft >= CraftSimulator.QuarterSeconds(action) / 4.0, $"step {i}: {recommendation.SecondsLeft}");
            estimates.Add(recommendation.SecondsLeft);

            var success = random.Next(100) < CraftSimulator.SuccessPercent(state, action, condition);
            CraftSimulator.Use(parameters, state, action, condition, success, out state);
            advisor.Observe(action);
            if (CraftSimulator.SpendsStep(action))
                condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle(), false);
        }

        Assert.True(estimates.Count > 10);
        Assert.InRange(estimates[0], 30, 300);              // a whole expert craft: half a minute to a few
        Assert.True(estimates[^1] <= 5, $"the last action still claimed {estimates[^1]} s");
        Assert.True(estimates[^1] < estimates[0] / 4);
    }

    /// <summary>Plays a whole simulated craft with the advisor at the wheel, the way the session would drive it.</summary>
    private static (CraftState End, List<CraftAction> Pressed) Play(ExpertProblem problem, ExpertAdvisor advisor, int seed, int stock)
    {
        var parameters = problem.Parameters;
        var random = new Random(seed);
        var odds = ConditionOdds.ForRecipe(problem.ConditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(problem.ConditionsFlag);
        var state = ExpertPlayer.Start(problem, stock);
        var condition = Condition.Normal;
        var pressed = new List<CraftAction>();

        for (var i = 0; i < 200 && !CraftSimulator.IsFinal(parameters, state); i++)
        {
            var recommendation = advisor.Recommend(state, condition);
            Assert.NotNull(recommendation.Action);
            var action = recommendation.Action!.Value;
            Assert.Equal(ActionError.None, CraftSimulator.Check(parameters, state, action, condition));

            var success = random.Next(100) < CraftSimulator.SuccessPercent(state, action, condition);
            CraftSimulator.Use(parameters, state, action, condition, success, out state);
            advisor.Observe(action);
            pressed.Add(action);
            if (CraftSimulator.SpendsStep(action))
                condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle(), state.MaterialMiracle > 0);
        }

        return (state, pressed);
    }

    [Fact]
    public void ItBringsEveryMissionCraftToAnEnd_WithLegalActionsOnly()
    {
        foreach (var mission in MasterMission.All)
        {
            for (var seed = 1; seed <= 3; seed++)
            {
                var advisor = new ExpertAdvisor(mission.Problem, () => ExpertTuningBook.Preset(mission.Problem), Search, TimeSpan.Zero, seed: (ulong)seed);
                var (end, _) = Play(mission.Problem, advisor, seed, stock: mission.StartUses);
                Assert.True(CraftSimulator.IsFinal(mission.Problem.Parameters, end), $"{mission.Name}, seed {seed}: never ended");
            }
        }
    }

    [Fact]
    public void OneCraft_TakesNoMoreOfTheMissionsDutyActionThanItIsAllowed()
    {
        foreach (var mission in MasterMission.All)
        {
            for (var seed = 1; seed <= 4; seed++)
            {
                // The whole stock is on the table (as the game shows it); the knob says one per craft.
                var advisor = new ExpertAdvisor(mission.Problem, () => ExpertTuning.Default, search: null, TimeSpan.Zero);
                var (_, pressed) = Play(mission.Problem, advisor, seed, stock: 4);
                Assert.InRange(pressed.Count(a => a == mission.Duty), 0, 1);
            }
        }
    }

    [Fact]
    public void TheCapHolds_EvenWhenTheUseWasNeverReported()
    {
        // A press made by hand may not come past the action hook: the stock going down has to be enough.
        var mission = MasterMission.All[2];
        var problem = mission.Problem;
        var advisor = new ExpertAdvisor(problem, () => ExpertTuning.Default, search: null, TimeSpan.Zero);

        var state = ExpertPlayer.Start(problem, 4);
        Assert.Equal(CraftAction.MaterialMiracle, advisor.Recommend(state, Condition.Normal).Action);

        // Used, expired, never Observed: three left, none running.
        state.MaterialMiracleCharges = 3;
        Assert.NotEqual(CraftAction.MaterialMiracle, advisor.Recommend(state, Condition.Normal).Action);
    }

    [Fact]
    public void AnActionTheGameRefuses_IsNotRecommendedAgain()
    {
        var problem = MasterMission.All[2].Problem;
        var advisor = new ExpertAdvisor(problem, () => ExpertTuning.Default, Search, TimeSpan.Zero);
        var state = ExpertPlayer.Start(problem, 0);

        var first = advisor.Recommend(state, Condition.Normal).Action!.Value;
        var second = advisor.Recommend(state, Condition.Normal, ActionMask.With(0, first)).Action;

        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.Equal(ActionError.None, CraftSimulator.Check(problem.Parameters, state, second!.Value, Condition.Normal));
    }

    [Fact]
    public void WithNoBudget_TheSameSeedGivesTheSameCraft()
    {
        var mission = MasterMission.All[1];
        var first = Play(mission.Problem, new ExpertAdvisor(mission.Problem, () => ExpertTuning.Default, Search, TimeSpan.Zero, seed: 7), 11, 1);
        var second = Play(mission.Problem, new ExpertAdvisor(mission.Problem, () => ExpertTuning.Default, Search, TimeSpan.Zero, seed: 7), 11, 1);

        Assert.Equal(first.Pressed, second.Pressed);
        Assert.Equal(first.End, second.End);
    }

    [Fact]
    public void ABudgetThatIsAlreadySpent_StillAnswers()
    {
        var problem = MasterMission.All[0].Problem;
        var advisor = new ExpertAdvisor(problem, () => ExpertTuning.Default, new SearchSettings(Samples: 512), TimeSpan.FromTicks(1));
        var state = ExpertPlayer.Start(problem, 1);

        var answer = advisor.Recommend(state, Condition.Normal);
        Assert.NotNull(answer.Action);
        Assert.Contains("Expert solver", answer.Reason);
    }

    [Fact]
    public void OnAFinishedCraft_ThereIsNothingToPress()
    {
        var problem = MasterMission.All[0].Problem;
        var advisor = new ExpertAdvisor(problem, () => ExpertTuning.Default, Search, TimeSpan.Zero);
        var done = ExpertPlayer.Start(problem, 0);
        done.Progress = problem.Parameters.MaxProgress;

        Assert.Null(advisor.Recommend(done, Condition.Normal).Action);
    }
}
