using Hephaestus.Features.Crafting.Planner;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Tests.Features.Crafting.Versus;

namespace Hephaestus.Tests.Features.Crafting.Planner;

/// <summary>
/// What must hold for the planner's numbers to mean anything: a plan is made of actions the simulator accepts in
/// that order, it says truthfully where it ends, and the same question gets the same answer. How SHORT its plans
/// are beside Raphael's is PlannerBenchmark's business.
/// </summary>
public class RotationPlannerTests
{
    private static readonly PlannerSettings Small = new(Beam: 400);

    [Theory]
    [InlineData("Ingot770-Solid")]
    [InlineData("Gemdraught770-Solid-HqMats")]
    [InlineData("Gemdraught770-Solid-NqMats")]
    public void APlan_IsPlayable_AndEndsWhereItSaysItEnds(string name)
    {
        var scenario = VersusScenarios.Named(name);
        var plan = RotationPlanner.Solve(scenario.Parameters, scenario.InitialQuality, scenario.TargetQuality, Small);

        Assert.True(plan.Completed, "no finished rotation was found");
        var played = RotationPlayer.PlayNormal(scenario.Parameters, plan.Actions);
        Assert.Equal(0, played.StepsSkipped);
        Assert.True(played.Completed);
        Assert.Equal(plan.Quality, Math.Min(scenario.Parameters.MaxQuality, played.Quality + scenario.InitialQuality));
        Assert.Equal(plan.Reached, plan.Quality >= scenario.TargetQuality);
    }

    [Fact]
    public void NothingInAPlan_IsLeftToChance()
    {
        var scenario = VersusScenarios.Named("Gemdraught770-Solid-HqMats");
        var plan = RotationPlanner.Solve(scenario.Parameters, scenario.InitialQuality, scenario.TargetQuality, Small);

        Assert.All(plan.Actions, action =>
        {
            Assert.Equal(100, CraftSimulator.SuccessPercent(action, Condition.Normal));
            Assert.DoesNotContain(action, new[] { CraftAction.TricksOfTheTrade, CraftAction.PreciseTouch, CraftAction.IntensiveSynthesis });
        });
    }

    [Fact]
    public void TheSameQuestion_GetsTheSameAnswer()
    {
        var scenario = VersusScenarios.Named("Ingot770-Solid");
        var first = RotationPlanner.Solve(scenario.Parameters, scenario.InitialQuality, scenario.TargetQuality, Small);
        var second = RotationPlanner.Solve(scenario.Parameters, scenario.InitialQuality, scenario.TargetQuality, Small);

        Assert.Equal(first.Actions, second.Actions);
    }

    [Fact]
    public void AnEasyTarget_IsReached_AndATargetAlreadyMetNeedsOnlyTheProgress()
    {
        var scenario = VersusScenarios.Named("Gemdraught770-Solid-HqMats");
        var parameters = scenario.Parameters;

        var met = RotationPlanner.Solve(parameters, initialQuality: parameters.MaxQuality, target: parameters.MaxQuality, Small);
        Assert.True(met.Reached);

        var full = RotationPlanner.Solve(parameters, scenario.InitialQuality, scenario.TargetQuality, Small);
        Assert.True(met.Steps < full.Steps || !full.Reached, "an item that needs no quality should take fewer steps");
    }

    // ---- the polisher ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Ingot770-Solid")]
    [InlineData("Gemdraught770-Solid-HqMats")]
    [InlineData("Gemdraught770-Solid-NqMats")]
    public void Polishing_GivesBackAPlayableRotation_ThatIsNoWorseThanWhatItWasGiven(string name)
    {
        var scenario = VersusScenarios.Named(name);
        var parameters = scenario.Parameters;
        var rough = RotationPlanner.Solve(parameters, scenario.InitialQuality, scenario.TargetQuality, Small);
        var polished = PlanPolisher.Polish(parameters, scenario.InitialQuality, scenario.TargetQuality, rough.Actions, iterations: 20_000, restarts: 1);

        var played = RotationPlayer.PlayNormal(parameters, polished.Actions);
        Assert.Equal(0, played.StepsSkipped);
        Assert.True(played.Completed);
        Assert.Equal(polished.Quality, Math.Min(parameters.MaxQuality, played.Quality + scenario.InitialQuality));

        // One of its runs starts from the rotation it was given and never forgets the best it has seen.
        Assert.True(polished.Reached || !rough.Reached, "polishing lost the target");
        if (rough.Reached)
            Assert.True(polished.Steps <= rough.Steps, $"polishing made it longer: {rough.Steps} -> {polished.Steps}");
        else
            Assert.True(polished.Reached || polished.Quality >= rough.Quality, $"polishing lost quality: {rough.Quality} -> {polished.Quality}");
    }

    [Fact]
    public void Polishing_IsRepeatable()
    {
        var scenario = VersusScenarios.Named("Gemdraught770-Solid-HqMats");
        var rough = RotationPlanner.Solve(scenario.Parameters, scenario.InitialQuality, scenario.TargetQuality, Small);
        var first = PlanPolisher.Polish(scenario.Parameters, scenario.InitialQuality, scenario.TargetQuality, rough.Actions, 20_000, 1, rngSeed: 3);
        var second = PlanPolisher.Polish(scenario.Parameters, scenario.InitialQuality, scenario.TargetQuality, rough.Actions, 20_000, 1, rngSeed: 3);

        Assert.Equal(first.Actions, second.Actions);
    }
}
