using Hephaestus.Features.Crafting.Simulation;
using ArtisanCondition = Artisan.CraftingLogic.CraftData.Condition;
using ArtisanConditionFlags = Artisan.CraftingLogic.CraftData.ConditionFlags;
using ArtisanCraft = Artisan.CraftingLogic.CraftState;
using ArtisanSolver = Artisan.CraftingLogic.Solver;
using ArtisanStep = Artisan.CraftingLogic.StepState;
using MacroSettings = Artisan.CraftingLogic.Solvers.MacroSolverSettings;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Hephaestus.Tests.Features.Crafting.Versus;

/// <summary>
/// Artisan at the wheel: one of ITS solvers (the vendored sources under External/Artisan, logic
/// untouched) deciding every action. This class is only the plumbing Artisan's GameInterop/Crafting.cs
/// does in game — <see cref="Step"/> mirrors <c>BuildStepState</c> field for field: what the game
/// shows comes from the shared craft state, and what Artisan remembers between steps (the previous
/// action, whether it failed, the observe streak, the expert solver's two flags) is carried here.
/// </summary>
internal sealed class ArtisanDriver : IVersusDriver
{
    private readonly ArtisanCraft _craft;
    private ArtisanSolver _solver;

    private int _index = 1; // the Synthesis window's step number
    private Skills _previous = Skills.None;
    private bool _previousFailed;
    private ArtisanCondition _previousCondition = ArtisanCondition.Normal;
    private int _observeStreak;
    private bool _expertEmergency;
    private bool _expertMiracleTrigger;

    public ArtisanDriver(ArtisanCraft craft, ArtisanSolver solver)
    {
        _craft = craft;
        _solver = solver;
    }

    /// <summary>An independent copy, mid-craft: for playing "what would Artisan do from here" without disturbing the real craft.</summary>
    public ArtisanDriver Clone()
    {
        var copy = (ArtisanDriver)MemberwiseClone();
        copy._solver = _solver.Clone();
        return copy;
    }

    /// <summary>Artisan's own description of a craft, as <c>Crafting.BuildCraftStateForRecipe</c> fills it for a non-collectable recipe.</summary>
    public static ArtisanCraft Craft(VersusScenario scenario)
    {
        var recipe = scenario.Recipe;
        var craft = new ArtisanCraft
        {
            StatCraftsmanship = scenario.Craftsmanship,
            StatControl = scenario.Control,
            StatCP = scenario.Cp,
            StatLevel = scenario.JobLevel,
            UnlockedManipulation = true,
            Specialist = false,
            SplendorCosmic = scenario.CosmicTools,
            CraftCollectible = false,
            CraftExpert = recipe.Expert,
            CraftLevel = recipe.ClassJobLevel,
            CraftDurability = recipe.Durability,
            CraftProgress = recipe.Progress,
            CraftProgressDivider = recipe.ProgressDivider,
            CraftProgressModifier = recipe.ProgressModifier,
            CraftQualityDivider = recipe.QualityDivider,
            CraftQualityModifier = recipe.QualityModifier,
            CraftQualityMax = recipe.Quality,
            CraftRequiredQuality = recipe.RequiredQuality,
            CraftHQ = recipe.CanHq,
            IsCosmic = false,
            ConditionFlags = (ArtisanConditionFlags)recipe.ConditionsFlag,
            CraftConditionProbabilities = ConditionOdds.ForRecipe(recipe.ConditionsFlag, scenario.JobLevel),
            InitialQuality = scenario.InitialQuality,
        };

        if (recipe.RequiredQuality > 0)
        {
            craft.CraftQualityMin1 = craft.CraftQualityMin2 = craft.CraftQualityMin3 = craft.CraftQualityMax = recipe.RequiredQuality;
        }
        else if (recipe.CollectableTiers is { Length: 3 } tiers)
        {
            // A Cosmic Exploration collectable. Artisan's expert solver then aims at the top tier, or at full quality
            // while its "max quality on Cosmic recipes" setting is on - which is how it ships.
            craft.CraftCollectible = true;
            craft.IsCosmic = true;
            (craft.CraftQualityMin1, craft.CraftQualityMin2, craft.CraftQualityMin3) = (tiers[0], tiers[1], tiers[2]);
        }
        else if (recipe.CanHq)
            craft.CraftQualityMin3 = craft.CraftQualityMax;
        return craft;
    }

    /// <summary>A Raphael rotation as Artisan stores one: a macro with every option off (RaphaelSolver.cs).</summary>
    public static MacroSettings.Macro Macro(IReadOnlyList<CraftAction> rotation)
    {
        var macro = new MacroSettings.Macro { Name = "Raphael" };
        foreach (var action in rotation)
            macro.Steps.Add(new MacroSettings.MacroStep { Action = ToSkill(action) });
        return macro;
    }

    public VersusMove? Next(in CraftState state, Condition condition, int finalAppraisalLeft)
    {
        var step = Step(state, condition, finalAppraisalLeft);
        var skill = _solver.Solve(_craft, step).Action;

        // The expert solver writes these two on the step it is handed; Artisan carries them to the next.
        _expertEmergency = step.ExpertEmergency;
        _expertMiracleTrigger = step.ExpertMiracleTrigger;

        if (skill == Skills.None)
            return null;
        if (skill == Skills.FinalAppraisal)
            return VersusMove.Appraise;
        return ToAction(skill) is { } action ? new VersusMove(action) : null; // null: an action this benchmark gives neither side
    }

    public void Observed(VersusMove move, Condition takenUnder, bool success)
    {
        var skill = move.FinalAppraisal ? Skills.FinalAppraisal : ToSkill(move.Action);
        if (!move.FinalAppraisal && CraftSimulator.SpendsStep(move.Action))
            _index++;

        _previous = skill;
        _previousFailed = !success;
        _previousCondition = (ArtisanCondition)(int)takenUnder;
        _observeStreak = skill == Skills.Observe ? _observeStreak + 1 : 0;
    }

    /// <summary>The step exactly as the solver would be handed it right now - for comparing the two simulators from one state.</summary>
    internal ArtisanStep Snapshot(in CraftState state, Condition condition) => Step(state, condition, 0);

    private ArtisanStep Step(in CraftState state, Condition condition, int finalAppraisalLeft) => new()
    {
        Index = _index,
        Progress = state.Progress,
        Quality = state.Quality,
        Durability = state.Durability,
        RemainingCP = state.Cp,
        Condition = (ArtisanCondition)(int)condition, // the two enums share the game's values
        PrevCondition = _previousCondition,
        IQStacks = state.InnerQuiet,
        WasteNotLeft = state.WasteNot,
        ManipulationLeft = state.Manipulation,
        GreatStridesLeft = state.GreatStrides,
        InnovationLeft = state.Innovation,
        VenerationLeft = state.Veneration,
        MuscleMemoryLeft = state.MuscleMemory,
        FinalAppraisalLeft = finalAppraisalLeft,
        CarefulObservationLeft = 0, // specialist only
        HeartAndSoulActive = state.HeartAndSoulActive,
        HeartAndSoulAvailable = state.HeartAndSoulAvailable,
        TrainedPerfectionActive = state.TrainedPerfectionActive,
        TrainedPerfectionAvailable = state.TrainedPerfectionAvailable,
        QuickInnoAvailable = state.QuickInnovationAvailable,
        QuickInnoLeft = state.QuickInnovationAvailable ? 1 : 0,
        ExpedienceLeft = state.Expedience ? 1 : 0,
        PrevActionFailed = _previousFailed,
        PrevComboAction = _previous,
        ObserveCounter = _observeStreak,
        ExpertEmergency = _expertEmergency,
        ExpertMiracleTrigger = _expertMiracleTrigger,
    };

    public static Skills ToSkill(CraftAction action) => action switch
    {
        CraftAction.BasicSynthesis => Skills.BasicSynthesis,
        CraftAction.BasicTouch => Skills.BasicTouch,
        CraftAction.MasterMend => Skills.MastersMend,
        CraftAction.Observe => Skills.Observe,
        CraftAction.TricksOfTheTrade => Skills.TricksOfTrade,
        CraftAction.WasteNot => Skills.WasteNot,
        CraftAction.Veneration => Skills.Veneration,
        CraftAction.StandardTouch => Skills.StandardTouch,
        CraftAction.GreatStrides => Skills.GreatStrides,
        CraftAction.Innovation => Skills.Innovation,
        CraftAction.WasteNot2 => Skills.WasteNot2,
        CraftAction.ByregotsBlessing => Skills.ByregotsBlessing,
        CraftAction.PreciseTouch => Skills.PreciseTouch,
        CraftAction.MuscleMemory => Skills.MuscleMemory,
        CraftAction.CarefulSynthesis => Skills.CarefulSynthesis,
        CraftAction.Manipulation => Skills.Manipulation,
        CraftAction.PrudentTouch => Skills.PrudentTouch,
        CraftAction.AdvancedTouch => Skills.AdvancedTouch,
        CraftAction.Reflect => Skills.Reflect,
        CraftAction.PreparatoryTouch => Skills.PreparatoryTouch,
        CraftAction.Groundwork => Skills.Groundwork,
        CraftAction.DelicateSynthesis => Skills.DelicateSynthesis,
        CraftAction.IntensiveSynthesis => Skills.IntensiveSynthesis,
        CraftAction.TrainedEye => Skills.TrainedEye,
        CraftAction.HeartAndSoul => Skills.HeartAndSoul,
        CraftAction.PrudentSynthesis => Skills.PrudentSynthesis,
        CraftAction.TrainedFinesse => Skills.TrainedFinesse,
        CraftAction.RefinedTouch => Skills.RefinedTouch,
        CraftAction.QuickInnovation => Skills.QuickInnovation,
        CraftAction.ImmaculateMend => Skills.ImmaculateMend,
        CraftAction.TrainedPerfection => Skills.TrainedPerfection,
        CraftAction.RapidSynthesis => Skills.RapidSynthesis,
        CraftAction.HastyTouch => Skills.HastyTouch,
        CraftAction.DaringTouch => Skills.DaringTouch,
        CraftAction.StellarSteadyHand => Skills.SteadyHand,
        CraftAction.MaterialMiracle => Skills.MaterialMiracle,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "no Artisan skill for this action"),
    };

    /// <summary>Null for the skills the simulator does not model (Careful Observation, Final Appraisal). The Cosmic pair maps, but this world never deals their charges.</summary>
    public static CraftAction? ToAction(Skills skill)
    {
        foreach (var action in Enum.GetValues<CraftAction>())
        {
            if (ToSkill(action) == skill)
                return action;
        }

        return null;
    }
}
