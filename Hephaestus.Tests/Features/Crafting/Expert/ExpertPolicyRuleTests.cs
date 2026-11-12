using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Tests.Features.Crafting.Versus;
using Hephaestus.Tests.Sandbox.Expert;

namespace Hephaestus.Tests.Features.Crafting.Expert;

/// <summary>
/// The rules added on 2026-11-12 - each one a decision the search kept taking out of the policy's hands - pinned
/// for what they are meant to do. How much they are WORTH is the benchmarks' business.
/// </summary>
public class ExpertPolicyRuleTests
{
    private static readonly VersusScenario Aqueduct = VersusScenarios.Named("AqueductFragment776-Melded");
    private static readonly VersusScenario Partisan = VersusScenarios.Named("Gemdraught770-Melded-NqMats");

    /// <summary>One craft, action by action: (action, condition it was pressed under).</summary>
    private static List<(CraftAction Action, Condition Condition)> Trace(ExpertProblem problem, ExpertTuning tuning, int seed)
    {
        var parameters = problem.Parameters;
        var random = new Random(seed);
        var odds = ConditionOdds.ForRecipe(problem.ConditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(problem.ConditionsFlag);
        var state = ExpertPlayer.Start(problem, 0);
        var condition = Condition.Normal;
        var trace = new List<(CraftAction, Condition)>();
        for (var n = 0; n < 250 && !CraftSimulator.IsFinal(parameters, state); n++)
        {
            var action = ExpertPolicy.Decide(parameters, tuning, problem.Aim(tuning), state, condition, problem.Tiers);
            trace.Add((action, condition));
            var success = random.Next(100) < CraftSimulator.SuccessPercent(state, action, condition);
            Assert.Equal(ActionError.None, CraftSimulator.Use(parameters, state, action, condition, success, out var next));
            state = next;
            if (CraftSimulator.SpendsStep(action))
                condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle(), false);
        }

        Assert.True(CraftSimulator.IsFinal(parameters, state), $"{problem.Name}, seed {seed}: the craft did not end");
        return trace;
    }

    [Fact]
    public void ASpecialist_PressesHeartAndSoul_ForTheActionItOpens_AndThatActionComesNext()
    {
        var pressed = 0;
        foreach (var scenario in new[] { Aqueduct, Partisan })
        {
            var problem = SandboxProblems.From(scenario, null, specialist: true);
            for (var seed = 1; seed <= 40; seed++)
            {
                var trace = Trace(problem, ExpertTuning.Default, seed);
                var at = trace.FindIndex(step => step.Action == CraftAction.HeartAndSoul);
                if (at < 0)
                    continue;

                pressed++;
                Assert.Equal(at, trace.FindLastIndex(step => step.Action == CraftAction.HeartAndSoul)); // once a craft
                Assert.True(at + 1 < trace.Count, "Heart and Soul was the last thing pressed");
                Assert.Contains(trace[at + 1].Action, new[] { CraftAction.IntensiveSynthesis, CraftAction.PreciseTouch });
                Assert.NotEqual(Condition.Good, trace[at].Condition); // on a Good step the three are open for free
            }
        }

        Assert.True(pressed >= 40, $"Heart and Soul was pressed in only {pressed} of 80 crafts");
    }

    [Fact]
    public void QuickInnovation_IsOnlyEverTheStepBeforeTheBlessing()
    {
        var pressed = 0;
        foreach (var scenario in new[] { Aqueduct, Partisan })
        {
            var problem = SandboxProblems.From(scenario, null, specialist: true);
            for (var seed = 1; seed <= 60; seed++)
            {
                var trace = Trace(problem, ExpertTuning.Default, seed);
                var at = trace.FindIndex(step => step.Action == CraftAction.QuickInnovation);
                if (at < 0)
                    continue;

                pressed++;
                Assert.True(at + 1 < trace.Count);
                Assert.Equal(CraftAction.ByregotsBlessing, trace[at + 1].Action);
            }
        }

        Assert.True(pressed > 0, "Quick Innovation was never pressed");
    }

    [Fact]
    public void WithoutADelineation_NeitherIsEverPressed()
    {
        var problem = SandboxProblems.From(Aqueduct, null, specialist: false);
        for (var seed = 1; seed <= 20; seed++)
            Assert.DoesNotContain(Trace(problem, ExpertTuning.Default, seed), step => step.Action is CraftAction.HeartAndSoul or CraftAction.QuickInnovation);
    }

    [Fact]
    public void ASpecialistsCraft_EndsWhateverTheTwoKnobsSay()
    {
        foreach (var scenario in new[] { Aqueduct, Partisan })
        {
            var problem = SandboxProblems.From(scenario, null, specialist: true);
            foreach (var knob in ExpertTuning.Knobs.Where(k => k.Knob is Knob.HeartAndSoul or Knob.QuickInnovation or Knob.Style))
            {
                foreach (var choice in knob.Choices)
                {
                    for (var seed = 1; seed <= 6; seed++)
                        Trace(problem, ExpertTuning.Default.With(knob.Knob, choice), seed);
                }
            }
        }
    }

    [Fact]
    public void TrainedPerfection_PricedByWhatItSaves_GoesToGroundwork_NotToTheNextRapidSynthesis()
    {
        var problem = SandboxProblems.From(Aqueduct, null, specialist: false);
        var parameters = problem.Parameters;
        var state = CraftSimulator.Initial(parameters);
        state.Combo = Combo.None;
        state.TrainedPerfectionAvailable = false;
        state.TrainedPerfectionActive = true;
        state.Veneration = 3;
        state.Cp = 500;

        CraftAction Press(int perfection)
            => ExpertPolicy.Decide(parameters, ExpertTuning.Default.With(Knob.Perfection, perfection), problem.Target, state, Condition.Normal);

        Assert.Equal(CraftAction.Groundwork, Press(1));
        Assert.Equal(CraftAction.RapidSynthesis, Press(0)); // as it was: the free action goes to whatever wears the item next
    }

    [Fact]
    public void AnOrdinaryItem_IsWorthItsHqChance_AndABrokenOneNothing()
    {
        var problem = SandboxProblems.From(Partisan);
        Assert.True(problem.ByHqChance);
        var parameters = problem.Parameters;

        CraftState End(int quality, bool finished) => new()
        {
            Progress = finished ? parameters.MaxProgress : 0,
            Durability = finished ? 10 : 0,
            Quality = quality,
        };

        Assert.Equal(1.0, ExpertSearch.Value(problem, End(parameters.MaxQuality, true), 0, 0), 6);
        Assert.Equal(0.25 + 0.75 * 0.76, ExpertSearch.Value(problem, End(17491, true), 0, 0), 6); // the user's 76% item
        Assert.Equal(0.0, ExpertSearch.Value(problem, End(17491, false), 0, 0), 6);

        // Time has its price where the craft has one: forty seconds at eight points a second.
        Assert.Equal(1.0 - 0.32, ExpertSearch.Value(problem, End(parameters.MaxQuality, true), 160, 8), 6);

        // An item with a demand stays all-or-nothing.
        var demand = SandboxProblems.From(Aqueduct);
        Assert.False(demand.ByHqChance);
        Assert.True(ExpertSearch.Value(demand, End(demand.Target - 1, true), 0, 0) < 0.3);
    }

    [Fact]
    public void OnlyAnItemJudgedByItsHqChance_MayBeAimedBelowTheFullBar()
    {
        var ninety = ExpertTuning.Default.With(Knob.Aim, 90);
        var ordinary = SandboxProblems.From(Partisan);
        var demand = SandboxProblems.From(Aqueduct);

        Assert.Equal(ordinary.Parameters.MaxQuality * 90 / 100, ordinary.Aim(ninety));
        Assert.Equal(ordinary.Target, ordinary.Aim(ExpertTuning.Default));
        Assert.Equal(demand.Target, demand.Aim(ninety));
    }
}
