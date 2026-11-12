using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Features.Crafting.Driving;

/// <summary>
/// What the game shows for the synthesis in progress, read in one go on the framework thread:
/// the Synthesis window's numbers, the condition, CP, and the crafter's buffs. Immutable, so it
/// can be handed to a background evaluation as-is.
///
/// Not everything the simulator needs is visible: the touch combo and whether the once-per-craft
/// actions are still available are remembered by <see cref="CraftSession"/> from the actions it
/// saw being used.
///
/// Cosmic Exploration: <paramref name="DutyAction"/> is the duty action the mission lends
/// (Stellar Steady Hand or Material Miracle; null outside such a mission) and
/// <paramref name="DutyCharges"/> its uses left. Material Miracle's remaining SECONDS are deliberately
/// not here - they change every frame, and a snapshot that never stops changing never settles; the
/// session is handed them beside the snapshot.
/// </summary>
public readonly record struct SynthesisSnapshot(
    uint RecipeId,
    int StepIndex,
    int Progress,
    int MaxProgress,
    int Quality,
    int MaxQuality,
    int Durability,
    int MaxDurability,
    Condition Condition,
    int Cp,
    int MaxCp,
    int InnerQuiet,
    int WasteNot,
    int Innovation,
    int Veneration,
    int GreatStrides,
    int MuscleMemory,
    int Manipulation,
    bool TrainedPerfectionActive,
    bool HeartAndSoulActive,
    bool Expedience,
    int SteadyHand = 0,
    bool MaterialMiracleActive = false,
    CraftAction? DutyAction = null,
    int DutyCharges = 0);

/// <summary>
/// Everything fixed for one craft, worked out once when it starts: the simulator's parameters, the
/// recipe's condition set, and the request to put to Raphael.
/// </summary>
/// <param name="Expert">
/// Set for an expert recipe the expert solver is to make: what the item must reach, its ratings if it is a scored
/// collectable, the duty action a Cosmic mission lends. Null = a planned rotation and the live evaluator.
/// </param>
/// <param name="Tight">
/// An ORDINARY recipe, as the expert solver would be given it: used when the planned rotation does not carry the item
/// to its target on paper (or there is no rotation) - there a fixed plan loses to a solver that works with the
/// conditions. Null = the live evaluator follows whatever rotation there is, as before 2026-11-11.
/// </param>
public readonly record struct CraftSetup(
    CraftParameters Parameters, ushort ConditionsFlag, BaselineRequest Request, ExpertProblem? Expert = null, ExpertProblem? Tight = null);
