using Artisan.CraftingLogic.Solvers;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Tests.Features.Crafting.Versus;

/// <summary>
/// What makes <see cref="HephaestusVersusArtisanBenchmark"/> a fair fight, pinned: the shared loop
/// plays Hephaestus exactly as production's harness does, the bridge hands Artisan's solvers a craft
/// they play correctly, and both sides really do meet the same conditions. Fast; always on.
/// </summary>
public class VersusTests
{
    private static readonly EvaluatorSettings Depth1 = new() { MaxDepth = 1, Budget = TimeSpan.MaxValue };

    private static VersusResult Play(VersusScenario scenario, int seed, IVersusDriver driver)
        => VersusPlayer.Play(scenario.Parameters, scenario.Recipe.ConditionsFlag, scenario.InitialQuality, seed, driver);

    private static ArtisanDriver Artisan(VersusScenario scenario, Func<Artisan.CraftingLogic.CraftState, Artisan.CraftingLogic.Solver> solver)
    {
        var craft = ArtisanDriver.Craft(scenario);
        return new ArtisanDriver(craft, solver(craft));
    }

    /// <summary>The shared loop is PolicyPlayer's loop: same dice, same order, same craft.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheSharedLoop_PlaysHephaestus_ExactlyAsPolicyPlayerDoes(int seed)
    {
        var scenario = VersusScenarios.Named("Ingot770-Solid");

        var expected = PolicyPlayer.Play(scenario.Parameters, scenario.Plan, scenario.Recipe.ConditionsFlag, new Random(seed), Depth1);
        var actual = Play(scenario, seed, new HephaestusDriver(scenario.Parameters, scenario.Plan, scenario.Recipe.ConditionsFlag, Depth1));

        Assert.Equal((expected.Completed, expected.Quality, expected.StepsUsed), (actual.Completed, actual.Quality, actual.Steps));
        Assert.Equal(expected.Final, actual.Final);
        Assert.Null(actual.Stalled);
    }

    /// <summary>
    /// Artisan replaying a Raphael rotation is the fixed rotation — on a non-expert recipe nothing a
    /// condition does can make a step unusable. If the bridge fed Artisan's macro solver a wrong
    /// state, or mapped an action wrongly, this is where it would show.
    /// </summary>
    [Fact]
    public void ArtisanReplayingARaphaelPlan_EndsExactlyWhereTheFixedRotationDoes()
    {
        var scenario = VersusScenarios.Named("Ingot770-Solid");

        for (var seed = 1; seed <= 25; seed++)
        {
            var expected = RotationPlayer.PlayRolled(scenario.Parameters, scenario.BackloadedPlan, scenario.Recipe.ConditionsFlag, new Random(seed));
            var actual = Play(scenario, seed, Artisan(scenario, craft => new MacroSolver(ArtisanDriver.Macro(scenario.BackloadedPlan), craft)));

            Assert.Null(actual.Stalled);
            Assert.Equal(expected.Final, actual.Final);
        }
    }

    /// <summary>The one-to-one claim itself: on one seed, every step-taking action of either side is taken under the condition the seed dealt that step.</summary>
    [Theory]
    [InlineData("Ingot770-Lean")]
    [InlineData("Reinforcement754-Solid")]
    public void BothSides_MeetTheSameCondition_OnTheSameStep(string name)
    {
        var scenario = VersusScenarios.Named(name);
        const int seed = 7;

        var ours = Play(scenario, seed, new HephaestusDriver(scenario.Parameters, scenario.Plan, scenario.Recipe.ConditionsFlag, Depth1));
        var theirs = Play(scenario, seed, scenario.Recipe.Expert
            ? Artisan(scenario, _ => new ExpertSolver())
            : Artisan(scenario, _ => new StandardSolver()));

        var dealt = VersusPlayer.Conditions(scenario.Recipe.ConditionsFlag, scenario.JobLevel, seed, 200);
        Assert.Equal(dealt.Take(ours.Steps), Steps(ours));
        Assert.Equal(dealt.Take(theirs.Steps), Steps(theirs));

        static IEnumerable<Condition> Steps(VersusResult run)
            => run.Trace.Where(step => step.Action != "FinalAppraisal").Select(step => step.Condition);
    }

    /// <summary>Artisan's two rule-based solvers play whole crafts through the bridge without asking for anything the world cannot do.</summary>
    [Theory]
    [InlineData("Ingot770-Solid")]
    [InlineData("Gemdraught770-Solid-HqMats")]
    [InlineData("SurveyPlank742-Solid")]
    [InlineData("OizysRarities759-Solid")]
    public void ArtisansOwnSolvers_PlayWholeCrafts(string name)
    {
        var scenario = VersusScenarios.Named(name);

        for (var seed = 1; seed <= 20; seed++)
        {
            var run = Play(scenario, seed, scenario.Recipe.Expert
                ? Artisan(scenario, _ => new ExpertSolver())
                : Artisan(scenario, _ => new StandardSolver()));

            Assert.True(run.Stalled == null, $"seed {seed}: {run.Stalled} after {HephaestusVersusArtisanBenchmark.Trace(run)}");
            Assert.True(CraftSimulator.IsFinal(scenario.Parameters, run.Final));
        }
    }

    /// <summary>Final Appraisal, as the world grants it: the craft cannot finish while it is up, and the buff is spent by the action it stopped.</summary>
    [Fact]
    public void FinalAppraisal_HoldsTheCraftOnePointShort_Once()
    {
        var scenario = VersusScenarios.Named("Ingot770-Solid");
        var parameters = scenario.Parameters with { MaxProgress = 300 }; // one Basic Synthesis would finish it
        var script = new Scripted(VersusMove.Appraise, new VersusMove(CraftAction.BasicSynthesis), new VersusMove(CraftAction.BasicSynthesis));

        var run = VersusPlayer.Play(parameters, scenario.Recipe.ConditionsFlag, 0, seed: 1, script);

        Assert.True(run.Completed);
        Assert.Equal(2, run.Steps);                           // the appraisal took no step
        Assert.Equal(scenario.Cp - 1, run.Final.Cp);          // and cost 1 CP
        Assert.Equal(new[] { 0, 299 }, script.ProgressSeen.Skip(1)); // held at 299 by the first synthesis, finished by the second
    }

    /// <summary>
    /// Artisan's simulator and ours, action by action from identical states: every number agrees -
    /// progress, quality, durability, CP, every buff - and the only differences are three rules, each
    /// known and explained here. Anything else appearing (a potency we changed, a newer Artisan that
    /// models something differently) is a real disagreement about the game and fails this test.
    ///   - Refined Touch without its Basic Touch: the game allows it (as a pricier Basic Touch); like
    ///     raphael-sim we never offer it, Artisan's simulator does.
    ///   - Hasty Touch while Expedience is up: the button IS Daring Touch then; we name it so.
    ///   - Advanced Touch after a Standard Touch that was NOT itself a combo: raphael-sim (and so we)
    ///     charge the full 46 CP, Artisan's simulator charges the combo's 18. Unsettled in game.
    /// </summary>
    [Theory]
    [InlineData("Ingot770-Lean")]
    [InlineData("OizysRarities759-Solid")]
    public void TheTwoSimulators_AgreeOnEveryNumber_AndDifferOnlyOnThreeKnownRules(string name)
    {
        var known = new HashSet<(string, CraftAction)>
        {
            ("usable", CraftAction.RefinedTouch),
            ("usable", CraftAction.HastyTouch),
            ("cp", CraftAction.AdvancedTouch),
            ("usable", CraftAction.AdvancedTouch),
        };

        var differences = SimulatorComparison.Run(VersusScenarios.Named(name), crafts: 400, seed: 1);

        var unexpected = differences.Where(d => !known.Contains((d.Kind, d.Action))).ToList();
        Assert.True(unexpected.Count == 0, unexpected.Count == 0 ? "" :
            $"{unexpected.Count} unexpected, first: {unexpected[0].Kind} after {unexpected[0].Action} under {unexpected[0].Condition}: ours {unexpected[0].Ours}, Artisan {unexpected[0].Theirs}, from [{unexpected[0].State}]");
        // The CP difference is always the combo discount Artisan grants and we do not: 46 - 18, halved under Pliant.
        Assert.All(differences.Where(d => d.Kind == "cp"), d => Assert.Contains(int.Parse(d.Theirs) - int.Parse(d.Ours), new[] { 28, 14 }));
    }

    private sealed class Scripted : IVersusDriver
    {
        private readonly Queue<VersusMove> _moves;

        public Scripted(params VersusMove[] moves) => _moves = new Queue<VersusMove>(moves);

        public List<int> ProgressSeen { get; } = new();

        public VersusMove? Next(in CraftState state, Condition condition, int finalAppraisalLeft)
        {
            ProgressSeen.Add(state.Progress);
            return _moves.Count > 0 ? _moves.Dequeue() : null;
        }

        public void Observed(VersusMove move, Condition takenUnder, bool success)
        {
        }
    }
}
