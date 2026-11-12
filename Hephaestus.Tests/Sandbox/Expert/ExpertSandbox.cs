using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Tests.Features.Crafting.Versus;

namespace Hephaestus.Tests.Sandbox.Expert;

/// <summary>The benchmark's scenarios as problems for the expert solver.</summary>
internal static class SandboxProblems
{
    /// <summary>HEPHAESTUS_SANDBOX_SPECIALIST=1: every problem is a specialist's, with a delineation for Heart and Soul and for Quick Innovation.</summary>
    public static bool SpecialistByDefault => Environment.GetEnvironmentVariable("HEPHAESTUS_SANDBOX_SPECIALIST") == "1";

    public static ExpertProblem From(VersusScenario scenario) => From(scenario, null);

    /// <param name="duty">The Cosmic duty action the mission lends; it is then allowed in the problem's parameters.</param>
    /// <param name="specialist">Heart and Soul and Quick Innovation are allowed; null = as the environment says.</param>
    public static ExpertProblem From(VersusScenario scenario, CraftAction? duty, bool? specialist = null)
    {
        var parameters = scenario.Parameters;
        if (duty is { } action)
            parameters = parameters with { AllowedActions = ActionMask.With(parameters.AllowedActions, action) };
        if (specialist ?? SpecialistByDefault)
        {
            parameters = parameters with
            {
                AllowedActions = ActionMask.With(ActionMask.With(parameters.AllowedActions, CraftAction.HeartAndSoul), CraftAction.QuickInnovation),
            };
        }
        // As the plugin's CraftSetupReader sets it: a scored collectable is made against the mission's clock.
        // ...and an ordinary item that can be high quality is judged by its HQ chance (the "tight" problem of the plugin).
        var recipe = scenario.Recipe;
        return new(scenario.Name, parameters, recipe.ConditionsFlag, scenario.InitialQuality, scenario.TargetQuality,
            recipe.CollectableTiers, duty, TimeValue: recipe.CollectableTiers != null ? ExpertTuningBook.TimeValue : 0,
            ByHqChance: !recipe.Expert && recipe.CanHq && recipe.RequiredQuality == 0 && recipe.CollectableTiers == null);
    }
}

/// <summary>The sandbox policy at the wheel of the shared world, so a craft can be traced action by action beside Artisan's.</summary>
internal sealed class ExpertPolicyDriver : IVersusDriver
{
    private readonly ExpertProblem _problem;
    private readonly ExpertTuning _tuning;
    private readonly SearchSettings? _search;
    private readonly float[] _odds;
    private readonly bool _expert;
    private ulong _searchSeed;

    public ExpertPolicyDriver(ExpertProblem problem, ExpertTuning tuning, SearchSettings? search = null, int seed = 1)
    {
        _problem = problem;
        _tuning = tuning;
        _search = search;
        _odds = ConditionOdds.ForRecipe(problem.ConditionsFlag, problem.Parameters.JobLevel);
        _expert = ConditionOdds.IsExpert(problem.ConditionsFlag);
        _searchSeed = (ulong)seed * 0xD1B54A32D192ED03UL;
    }

    public VersusMove? Next(in CraftState state, Condition condition, int finalAppraisalLeft)
        => new VersusMove(_search is null
            ? ExpertPolicy.Decide(_problem.Parameters, _tuning, _problem.Aim(_tuning), state, condition, _problem.Tiers)
            : ExpertSearch.Choose(_problem, _tuning, _odds, _expert, state, condition, _search, ref _searchSeed, out _));

    public void Observed(VersusMove move, Condition takenUnder, bool success)
    {
    }
}
