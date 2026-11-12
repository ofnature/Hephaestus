using Hephaestus.Features.Crafting.Planner;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Tests.Features.Crafting.Planner;

public class OwnPlanTests
{
    private static readonly BaselineRequest Request = new(
        RecipeLevel: 770, MaxProgress: 10040, MaxQuality: 21200, MaxDurability: 70, Expert: false, JobLevel: 100,
        BaseProgress: 314, BaseQuality: 301, Craftsmanship: 5911, Control: 5503, MaxCp: 641, InitialQuality: 10600,
        Manipulation: true, HeartAndSoul: false, QuickInnovation: false, BackloadProgress: false);

    [Fact]
    public void TheCraft_IsTheRequestsNumbers_WithNothingTheCrafterDoesNotHave()
    {
        var parameters = OwnPlan.Parameters(Request);

        Assert.Equal((641, 70, 10040, 21200, 314, 301, 100),
            (parameters.MaxCp, parameters.MaxDurability, parameters.MaxProgress, parameters.MaxQuality, parameters.BaseProgress, parameters.BaseQuality, parameters.JobLevel));
        Assert.True(ActionMask.Has(parameters.AllowedActions, CraftAction.Manipulation));
        Assert.False(ActionMask.Has(parameters.AllowedActions, CraftAction.TrainedEye));
        Assert.False(ActionMask.Has(parameters.AllowedActions, CraftAction.HeartAndSoul));
        Assert.False(ActionMask.Has(parameters.AllowedActions, CraftAction.MaterialMiracle));

        Assert.False(ActionMask.Has(OwnPlan.Parameters(Request with { Manipulation = false }).AllowedActions, CraftAction.Manipulation));
        Assert.True(ActionMask.Has(OwnPlan.Parameters(Request with { TrainedEye = true }).AllowedActions, CraftAction.TrainedEye));

        // A specialist's two, only where the request allows them (the player opted in and holds a delineation).
        Assert.False(ActionMask.Has(parameters.AllowedActions, CraftAction.QuickInnovation));
        var specialist = OwnPlan.Parameters(Request with { HeartAndSoul = true, QuickInnovation = true });
        Assert.True(ActionMask.Has(specialist.AllowedActions, CraftAction.HeartAndSoul));
        Assert.True(ActionMask.Has(specialist.AllowedActions, CraftAction.QuickInnovation));
    }

    [Fact]
    public void TheCacheKey_FollowsWhatTheAnswerDependsOn_AndIsNeverOneOfRaphaels()
    {
        var key = OwnPlan.CacheKey(Request);

        Assert.StartsWith($"own{OwnPlan.Version}|", key);
        Assert.NotEqual(key, RaphaelCommand.CacheKey(Request));
        Assert.NotEqual(key, OwnPlan.CacheKey(Request with { MaxCp = 741 }));
        Assert.NotEqual(key, OwnPlan.CacheKey(Request with { InitialQuality = 0 }));
        Assert.NotEqual(key, OwnPlan.CacheKey(Request with { Manipulation = false }));
        Assert.NotEqual(key, OwnPlan.CacheKey(Request with { TrainedEye = true }));
        Assert.NotEqual(key, OwnPlan.CacheKey(Request with { HeartAndSoul = true }));
        Assert.NotEqual(key, OwnPlan.CacheKey(Request with { QuickInnovation = true }));

        // What only Raphael is asked, or what is already in the base values, changes nothing.
        Assert.Equal(key, OwnPlan.CacheKey(Request with { BackloadProgress = true }));
        Assert.Equal(key, OwnPlan.CacheKey(Request with { Craftsmanship = 1, Control = 1, RecipeLevel = 1 }));
    }

    [Fact]
    public void AddingTrainedEyeToTheRequest_DidNotMoveRaphaelsCacheKeys()
    {
        Assert.Equal(RaphaelCommand.CacheKey(Request), RaphaelCommand.CacheKey(Request with { TrainedEye = true }));
    }

    [Fact]
    public void ARotationReaches_OnlyIfItFinishesTheItemAtTheTarget()
    {
        var parameters = OwnPlan.Parameters(Request);
        var finishOnly = new[]
        {
            CraftAction.WasteNot2, CraftAction.Veneration, CraftAction.Groundwork, CraftAction.Groundwork, CraftAction.Groundwork, CraftAction.Groundwork,
            CraftAction.Veneration, CraftAction.Groundwork, CraftAction.Groundwork,
        };
        var played = RotationPlayer.PlayNormal(parameters, finishOnly);
        Assert.True(played.Completed, "the fixture rotation should finish the item");

        Assert.True(OwnPlan.Reaches(parameters, finishOnly, initialQuality: 21200, target: 21200));   // nothing left to make
        Assert.False(OwnPlan.Reaches(parameters, finishOnly, initialQuality: 10600, target: 21200));  // finished, short
        Assert.False(OwnPlan.Reaches(parameters, new[] { CraftAction.Groundwork }, 21200, 21200));     // not finished
        Assert.False(OwnPlan.Reaches(parameters, Array.Empty<CraftAction>(), 21200, 21200));           // no rotation

        // Who makes the item: an item judged by its HQ chance is carried from 98% of the bar on; one with a demand only at the demand.
        var nearly = 21200 * OwnPlan.NearlyPercent / 100;
        Assert.True(OwnPlan.Carries(parameters, finishOnly, initialQuality: nearly, target: 21200, byHqChance: true));
        Assert.False(OwnPlan.Carries(parameters, finishOnly, initialQuality: nearly - 1, target: 21200, byHqChance: true));
        Assert.False(OwnPlan.Carries(parameters, finishOnly, initialQuality: nearly, target: 21200, byHqChance: false));
        Assert.True(OwnPlan.Carries(parameters, finishOnly, initialQuality: 21200, target: 21200, byHqChance: false));
    }
}
