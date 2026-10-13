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
    bool Expedience);

/// <summary>
/// Everything fixed for one craft, worked out once when it starts: the simulator's parameters, the
/// recipe's condition set, and the request to put to Raphael.
/// </summary>
public readonly record struct CraftSetup(CraftParameters Parameters, ushort ConditionsFlag, BaselineRequest Request);
