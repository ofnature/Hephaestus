using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Tests.Features.Crafting.Solver;

/// <summary>One real recipe and one real solved rotation, shared by the solver tests.</summary>
internal static class SolverFixtures
{
    /// <summary>Black Star Mask of Casting at the stats Raphael's in-game-verified test uses.</summary>
    public static readonly CraftParameters FullStats = new(601, 80, 6300, 11400, 238, 300, 94, ActionMask.Regular);

    /// <summary>
    /// The same recipe for a weaker crafter. The plan below no longer caps quality here (and runs
    /// out of CP for two of its steps), so there is real headroom for a policy that can react.
    /// </summary>
    public static readonly CraftParameters WeakStats = FullStats with { BaseQuality = 220, MaxCp = 560 };

    /// <summary>What raphael-cli v0.28.6 (built from source) solved for <see cref="FullStats"/>.</summary>
    public static readonly CraftAction[] Baseline =
    {
        MuscleMemory, Manipulation, Veneration, WasteNot2, Groundwork, Groundwork, Groundwork, BasicTouch,
        RefinedTouch, DelicateSynthesis, Innovation, PreparatoryTouch, BasicTouch, StandardTouch, AdvancedTouch,
        Innovation, DelicateSynthesis, TrainedFinesse, GreatStrides, ByregotsBlessing, BasicSynthesis,
    };

    public const ushort NormalRecipe = ConditionOdds.NormalRecipeFlags;

    /// <summary>The state after the plan's first <paramref name="steps"/> usable steps, all under Normal.</summary>
    public static CraftState AfterPlanSteps(CraftParameters parameters, int steps, Action<CraftAction>? onUsed = null)
    {
        var state = CraftSimulator.Initial(parameters);
        var used = 0;
        foreach (var action in Baseline)
        {
            if (used == steps)
                break;
            if (CraftSimulator.Use(parameters, state, action, Condition.Normal, true, out var next) != ActionError.None)
                continue;

            state = next;
            used++;
            onUsed?.Invoke(action);
        }

        return state;
    }
}
