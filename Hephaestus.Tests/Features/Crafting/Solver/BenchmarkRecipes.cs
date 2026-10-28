using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Tests.Features.Crafting.Solver;

/// <param name="Source">Where the baseline came from — "raphael-cli v0.28.6" or "hand-made".</param>
internal sealed record BenchmarkRecipe(string Name, CraftParameters Parameters, ushort ConditionsFlag, CraftAction[] Baseline, string Source);

/// <summary>
/// The recipe spread <see cref="LiveVersusFixedBenchmark"/> runs over.
///
/// Only the Black Star Mask baselines are real raphael-cli output. The rest are HAND-MADE: the
/// recipes are built from the simulator's parameter model (stats from raphael-sim's test vectors
/// where one exists, otherwise invented), and each rotation was found by a throwaway beam search
/// over OUR simulator — all-Normal conditions, no success-roll actions, maximise quality then CP
/// left. On the Black Star Mask that search lands about 3% below raphael-cli's quality, so read
/// these as strong plans, not optimal ones. Replace any of them with raphael-cli output when it is
/// at hand; the numbers in the table are what to feed it.
/// </summary>
internal static class BenchmarkRecipes
{
    private const ushort Normal = ConditionOdds.NormalRecipeFlags;

    private static CraftParameters Recipe(int cp, int durability, int progress, int quality, int baseProgress, int baseQuality, int level)
        => new(cp, durability, progress, quality, baseProgress, baseQuality, level, ActionMask.Regular);

    /// <summary>raphael-cli v0.28.6 for the Black Star Mask with its quality cap lifted — see RaphaelVectorTests.</summary>
    public static readonly CraftAction[] BlackStarUncappedPlan =
    {
        Reflect, Innovation, PrudentTouch, PrudentTouch, BasicTouch, StandardTouch, Manipulation, Innovation,
        WasteNot2, BasicTouch, StandardTouch, AdvancedTouch, Veneration, Groundwork, Groundwork, Groundwork,
        Groundwork, Innovation, Veneration, DelicateSynthesis, GreatStrides, ByregotsBlessing, CarefulSynthesis,
    };

    public static readonly BenchmarkRecipe[] All =
    {
        new("BlackStar94-Full", SolverFixtures.FullStats, Normal, SolverFixtures.Baseline, "raphael-cli v0.28.6"),
        new("BlackStar94-Weak", SolverFixtures.WeakStats, Normal, SolverFixtures.Baseline, "raphael-cli v0.28.6 (solved for Full)"),
        new("BlackStar94-OtherPlan", SolverFixtures.FullStats, Normal, BlackStarUncappedPlan, "raphael-cli v0.28.6 (quality cap lifted)"),

        // Hand-made from here on. Stats are raphael-sim's test vectors where the name is a real
        // item (Ironwood Spear, Skybuilders' Stone, Classical Index, Diadochos Wristband, Maraging
        // Steel Ingot, Rarefied Tacos), otherwise invented; the -TightCp / -DurNN variants change
        // only that number. Expert/Cosmic flags select ConditionOdds' measured tables.
        new("Low15", Recipe(180, 60, 90, 600, 20, 30, 15), Normal,
            new[]
            {
                BasicTouch, BasicTouch, BasicTouch, BasicTouch, WasteNot, BasicTouch, Veneration, BasicSynthesis,
                BasicSynthesis, BasicSynthesis,
            }, "hand-made"),
        new("Low35", Recipe(280, 70, 400, 1800, 42, 75, 35), Normal,
            new[]
            {
                Innovation, WasteNot, BasicTouch, StandardTouch, BasicSynthesis, BasicSynthesis, Innovation,
                BasicTouch, StandardTouch, WasteNot, StandardTouch, Veneration, BasicSynthesis, BasicSynthesis,
                BasicSynthesis, BasicSynthesis,
            }, "hand-made"),
        new("Low50", Recipe(350, 70, 900, 3500, 70, 110, 50), Normal,
            new[]
            {
                Innovation, BasicTouch, StandardTouch, BasicTouch, StandardTouch, MasterMend, GreatStrides, WasteNot2,
                ByregotsBlessing, Veneration, BasicSynthesis, BasicSynthesis, BasicSynthesis, BasicSynthesis,
                BasicSynthesis, BasicSynthesis, BasicSynthesis, BasicSynthesis, BasicSynthesis,
            }, "hand-made"),
        new("Mid70", Recipe(480, 70, 2800, 7500, 190, 250, 70), Normal,
            new[]
            {
                Reflect, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, Manipulation, Innovation,
                BasicTouch, StandardTouch, GreatStrides, ByregotsBlessing, Veneration, BasicSynthesis,
                CarefulSynthesis, CarefulSynthesis, MasterMend, Veneration, CarefulSynthesis, CarefulSynthesis,
                CarefulSynthesis, BasicSynthesis,
            }, "hand-made"),
        new("Mid70-Dur40", Recipe(480, 40, 2800, 7500, 190, 250, 70), Normal,
            new[]
            {
                Reflect, Innovation, PrudentTouch, PrudentTouch, PrudentTouch, Manipulation, Innovation, PrudentTouch,
                PrudentTouch, GreatStrides, ByregotsBlessing, Veneration, CarefulSynthesis, CarefulSynthesis,
                CarefulSynthesis, MasterMend, Veneration, CarefulSynthesis, CarefulSynthesis, BasicSynthesis,
                BasicSynthesis,
            }, "hand-made"),
        new("Skybuilders81-Dur60", Recipe(427, 60, 1080, 9900, 204, 253, 81), Normal,
            new[]
            {
                Reflect, Innovation, PrudentTouch, PrudentTouch, PrudentTouch, DelicateSynthesis, Manipulation,
                Innovation, BasicTouch, BasicTouch, StandardTouch, AdvancedTouch, GreatStrides, Innovation,
                ByregotsBlessing, Veneration, CarefulSynthesis, CarefulSynthesis,
            }, "hand-made"),
        new("Ironwood85", Recipe(500, 80, 3100, 6800, 240, 307, 85), Normal,
            new[]
            {
                Reflect, Innovation, BasicTouch, BasicTouch, StandardTouch, AdvancedTouch, Manipulation, PrudentTouch,
                GreatStrides, ByregotsBlessing, Veneration, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis,
                CarefulSynthesis, BasicSynthesis, BasicSynthesis,
            }, "hand-made"),
        new("Ironwood85-Dur40", Recipe(500, 40, 3100, 6800, 240, 307, 85), Normal,
            new[]
            {
                Reflect, Manipulation, WasteNot2, Innovation, PreparatoryTouch, PreparatoryTouch, PreparatoryTouch,
                ByregotsBlessing, Veneration, Groundwork, Groundwork, CarefulSynthesis, BasicSynthesis,
            }, "hand-made"),
        new("ClassicalIndex90", Recipe(594, 70, 3900, 10920, 247, 265, 90), Normal,
            new[]
            {
                Reflect, Innovation, BasicTouch, StandardTouch, AdvancedTouch, Manipulation, Innovation, Observe,
                AdvancedTouch, Veneration, PrudentTouch, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis,
                Innovation, Manipulation, BasicTouch, StandardTouch, AdvancedTouch, GreatStrides, Innovation,
                ByregotsBlessing, Veneration, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis,
            }, "hand-made"),
        new("Diadochos90", Recipe(700, 70, 6600, 14040, 248, 270, 90), Normal,
            new[]
            {
                Reflect, BasicTouch, StandardTouch, Manipulation, Innovation, WasteNot2, BasicTouch, StandardTouch,
                AdvancedTouch, Veneration, Groundwork, Groundwork, Groundwork, CarefulSynthesis, Innovation,
                Manipulation, BasicTouch, StandardTouch, AdvancedTouch, Innovation, PrudentTouch, GreatStrides,
                ByregotsBlessing, Veneration, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis,
            }, "hand-made"),
        new("Diadochos90-TightCp", Recipe(520, 70, 6600, 14040, 248, 270, 90), Normal,
            new[]
            {
                Reflect, Observe, AdvancedTouch, Manipulation, Innovation, WasteNot2, BasicTouch, StandardTouch,
                AdvancedTouch, Veneration, Groundwork, Groundwork, Groundwork, Groundwork, Innovation, Observe,
                AdvancedTouch, GreatStrides, ByregotsBlessing, Veneration, CarefulSynthesis, CarefulSynthesis,
            }, "hand-made"),
        new("Maraging100-Dur35", Recipe(540, 35, 4125, 12000, 282, 256, 100), Normal,
            new[]
            {
                Reflect, PrudentTouch, Innovation, Manipulation, BasicTouch, RefinedTouch, PrudentTouch,
                TrainedPerfection, Veneration, Groundwork, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis,
                Manipulation, Innovation, Observe, AdvancedTouch, Observe, AdvancedTouch, Innovation, Observe,
                AdvancedTouch, GreatStrides, ByregotsBlessing, BasicSynthesis,
            }, "hand-made"),
        new("Tacos100-Dur80", Recipe(697, 80, 6600, 12000, 261, 240, 100), Normal,
            new[]
            {
                Reflect, BasicTouch, RefinedTouch, DelicateSynthesis, TrainedPerfection, Veneration, Groundwork,
                DelicateSynthesis, PrudentSynthesis, PrudentSynthesis, Veneration, CarefulSynthesis, PrudentSynthesis,
                ImmaculateMend, PrudentSynthesis, Innovation, DelicateSynthesis, BasicTouch, StandardTouch,
                AdvancedTouch, Innovation, Observe, AdvancedTouch, TrainedFinesse, TrainedFinesse, Innovation,
                Observe, AdvancedTouch, GreatStrides, ByregotsBlessing, BasicSynthesis,
            }, "hand-made"),
        new("Tacos100-TightCp", Recipe(500, 80, 6600, 12000, 261, 240, 100), Normal,
            new[]
            {
                Reflect, PrudentTouch, TrainedPerfection, Veneration, Groundwork, DelicateSynthesis,
                DelicateSynthesis, DelicateSynthesis, Innovation, BasicTouch, StandardTouch, AdvancedTouch,
                ImmaculateMend, Innovation, Observe, AdvancedTouch, GreatStrides, ByregotsBlessing, BasicSynthesis,
                Veneration, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis, BasicSynthesis,
            }, "hand-made"),
        new("Expert90-115", Recipe(650, 70, 6000, 13000, 248, 270, 90), 115,
            new[]
            {
                Reflect, Innovation, PrudentTouch, DelicateSynthesis, Manipulation, PrudentTouch, Innovation,
                WasteNot2, BasicTouch, StandardTouch, AdvancedTouch, Veneration, Groundwork, Groundwork, Groundwork,
                Groundwork, Innovation, PrudentTouch, PrudentTouch, TrainedFinesse, GreatStrides, Innovation,
                ByregotsBlessing, CarefulSynthesis,
            }, "hand-made"),
        new("Expert100-483-Dur40", Recipe(600, 40, 5000, 11000, 261, 240, 100), 483,
            new[]
            {
                Reflect, DelicateSynthesis, TrainedPerfection, Innovation, PreparatoryTouch, Manipulation,
                PrudentTouch, PrudentTouch, GreatStrides, WasteNot2, Innovation, PreparatoryTouch, DelicateSynthesis,
                GreatStrides, ByregotsBlessing, Veneration, Groundwork, Groundwork, PrudentSynthesis,
                CarefulSynthesis,
            }, "hand-made"),
        new("Expert100-1011", Recipe(700, 80, 7500, 15000, 261, 240, 100), 1011,
            new[]
            {
                Reflect, Innovation, PreparatoryTouch, BasicTouch, StandardTouch, AdvancedTouch, TrainedPerfection,
                Innovation, PreparatoryTouch, PrudentTouch, Observe, AdvancedTouch, ImmaculateMend, Innovation,
                TrainedFinesse, TrainedFinesse, GreatStrides, ByregotsBlessing, WasteNot, Veneration, Groundwork,
                Groundwork, Groundwork, DelicateSynthesis, Veneration, CarefulSynthesis, CarefulSynthesis,
                PrudentSynthesis, CarefulSynthesis,
            }, "hand-made"),
        new("Cosmic100-1491", Recipe(600, 70, 5500, 12000, 282, 256, 100), 1491,
            new[]
            {
                Reflect, BasicTouch, RefinedTouch, Veneration, DelicateSynthesis, TrainedPerfection, Groundwork,
                DelicateSynthesis, Veneration, CarefulSynthesis, PrudentSynthesis, ImmaculateMend, CarefulSynthesis,
                Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, GreatStrides, Innovation, Observe,
                AdvancedTouch, TrainedFinesse, Innovation, GreatStrides, ByregotsBlessing, CarefulSynthesis,
            }, "hand-made"),
    };

    public static BenchmarkRecipe Named(string name) => All.Single(recipe => recipe.Name == name);
}
