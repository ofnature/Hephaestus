using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Tests.Features.Crafting.Versus;

namespace Hephaestus.Tests.Sandbox.Expert;

/// <summary>
/// What must hold for the sandbox's numbers to mean anything: the policy only ever presses legal
/// actions and always brings a craft to an end, the fast loop is the shared world's loop, and the
/// search and the tuner are repeatable. How GOOD the policy is, is the benchmark's business.
/// </summary>
public class ExpertSandboxTests
{
    private static readonly ExpertProblem[] Problems =
        VersusScenarios.All.Where(s => s.Recipe.Expert).Select(SandboxProblems.From).ToArray();

    [Fact]
    public void ThePolicy_BringsEveryCraftToAnEnd_OnEveryExpertRecipe()
    {
        Assert.NotEmpty(Problems);
        foreach (var problem in Problems)
        {
            for (var seed = 1; seed <= 60; seed++)
            {
                var run = ExpertPlayer.Play(problem, ExpertTuning.Default, seed);
                Assert.True(CraftSimulator.IsFinal(problem.Parameters, run.Final), $"{problem.Name}, seed {seed}: still going after {run.Steps} steps");
            }
        }
    }

    [Fact]
    public void ThePolicy_OnlyPressesLegalActions_WhateverTheKnobsSay()
    {
        // Every knob at each of its extremes in turn: a setting the tuner may try must never be able to stall a craft.
        foreach (var knob in ExpertTuning.Knobs)
        {
            foreach (var choice in new[] { knob.Choices[0], knob.Choices[^1] })
            {
                var tuning = ExpertTuning.Default.With(knob.Knob, choice);
                foreach (var problem in Problems)
                {
                    for (var seed = 1; seed <= 8; seed++)
                    {
                        var run = ExpertPlayer.Play(problem, tuning, seed);
                        Assert.True(CraftSimulator.IsFinal(problem.Parameters, run.Final), $"{problem.Name}, {knob.Knob}={choice}, seed {seed}");
                    }
                }
            }
        }
    }

    [Fact]
    public void TheFastLoop_PlaysTheSameCraftAsTheSharedWorld()
    {
        foreach (var scenario in VersusScenarios.All.Where(s => s.Recipe.Expert))
        {
            var problem = SandboxProblems.From(scenario);
            for (var seed = 1; seed <= 25; seed++)
            {
                var fast = ExpertPlayer.Play(problem, ExpertTuning.Default, seed);
                var shared = VersusPlayer.Play(scenario.Parameters, scenario.Recipe.ConditionsFlag, scenario.InitialQuality, seed,
                    new ExpertPolicyDriver(problem, ExpertTuning.Default));

                Assert.Null(shared.Stalled);
                Assert.Equal(shared.Final, fast.Final);
                Assert.Equal(shared.Steps, fast.Steps);
            }
        }
    }

    [Fact]
    public void TheSearch_IsRepeatable_AndPressesOnlyLegalActions()
    {
        var problem = Problems[0];
        var search = new SearchSettings(Samples: 32);
        var first = ExpertPlayer.Play(problem, ExpertTuning.Default, seed: 7, search);
        var second = ExpertPlayer.Play(problem, ExpertTuning.Default, seed: 7, search);

        Assert.Equal(first, second);
        Assert.True(CraftSimulator.IsFinal(problem.Parameters, first.Final));
    }

    [Fact]
    public void TheTuner_KeepsOnlyChangesThatHelpOnTheCraftsItTriedThemOn()
    {
        var problems = new[] { Problems[0] };
        var log = new List<string>();
        var tuned = ExpertTuner.Tune(problems, ExpertTuning.Default, crafts: 150, log.Add, maxRounds: 1);

        var before = ExpertTuner.Scores(problems, ExpertTuning.Default, 1, 150).Average();
        var after = ExpertTuner.Scores(problems, tuned, 1, 150).Average();
        Assert.True(after >= before, $"tuning made it worse: {before:F3} -> {after:F3} ({string.Join("; ", log)})");
    }

    // ---- Cosmic "Master" missions -----------------------------------------------------------------

    [Fact]
    public void WithADutyAction_ThePolicyStillEndsEveryCraft_WhateverTheKnobsSay()
    {
        foreach (var mission in MasterMission.All)
        {
            foreach (var knob in ExpertTuning.Knobs.Where(k => k.Knob is Knob.Aim or Knob.Reach or Knob.SteadyHand or Knob.Miracle or Knob.Style or Knob.Opener))
            {
                foreach (var choice in knob.Choices)
                {
                    var tuning = ExpertTuning.Default.With(knob.Knob, choice);
                    for (var uses = 0; uses <= 3; uses++)
                    {
                        for (var seed = 1; seed <= 6; seed++)
                        {
                            var run = ExpertPlayer.Play(mission.Problem, tuning, seed, dutyUses: uses);
                            Assert.True(CraftSimulator.IsFinal(mission.Problem.Parameters, run.Final), $"{mission.Name}, {knob.Knob}={choice}, {uses} uses, seed {seed}");
                            Assert.InRange(run.DutyUsed, 0, uses);
                            Assert.True(run.Quarters > 0);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void Points_FollowTheRatings_AndRiseAboveTheTopOne()
    {
        var problem = MasterMission.All[2].Problem; // 26,300 quality: ratings at 13,150 / 15,780 / 22,350
        Assert.Equal(0, problem.Points(completed: false, 26300));
        Assert.Equal(0, problem.Points(true, 13149));
        Assert.Equal(100, problem.Points(true, 13150));
        Assert.Equal(300, problem.Points(true, 15780));
        Assert.Equal(300, problem.Points(true, 22349));
        Assert.Equal(700, problem.Points(true, 22350));
        Assert.Equal(720, problem.Points(true, 22618)); // 86%
        Assert.Equal(1000, problem.Points(true, 26300));
    }

    [Fact]
    public void AMission_IsRepeatable_StaysInsideItsClock_AndNeverSpendsUsesItWasNotLent()
    {
        foreach (var mission in MasterMission.All)
        {
            for (var seed = 1; seed <= 5; seed++)
            {
                var first = MasterMissions.Play(mission, ExpertTuning.Default.With(Knob.DutyUses, 2), seed);
                Assert.Equal(first, MasterMissions.Play(mission, ExpertTuning.Default.With(Knob.DutyUses, 2), seed));

                var longest = mission.Seconds + mission.Checkpoints.Take(first.Stage).Sum(c => c.Seconds);
                var stock = mission.StartUses + mission.Checkpoints.Take(first.Stage).Sum(c => c.Uses);
                Assert.InRange(first.Seconds, 0, longest);
                Assert.InRange(first.DutyUsed, 0, stock);
                Assert.True(first.Score >= first.Points);
            }

            // Lent nothing, nothing is spent.
            Assert.Equal(0, MasterMissions.Play(mission, ExpertTuning.Default.With(Knob.DutyUses, 0), 1).DutyUsed);
        }
    }

    [Fact]
    public void Dice_AreUniformEnough_AndCopyingReplaysThem()
    {
        var dice = new Dice(42);
        var copy = dice;
        var low = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var percent = dice.Percent();
            Assert.Equal(percent, copy.Percent());
            var unit = dice.Unit();
            Assert.Equal(unit, copy.Unit());
            Assert.InRange(percent, 0, 99);
            Assert.InRange(unit, 0f, 0.9999999f);
            if (percent < 50)
                low++;
        }

        Assert.InRange(low, 9_600, 10_400);
    }
}
