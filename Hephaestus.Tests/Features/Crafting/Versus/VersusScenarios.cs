using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Tests.Features.Crafting.Versus;

/// <summary>A real recipe, as the game's Recipe and RecipeLevelTable sheets give it (client 2026.11.03).</summary>
/// <param name="RequiredQuality">Below this the craft fails outright; 0 = no such demand.</param>
/// <param name="CollectableTiers">
/// A Cosmic Exploration collectable's three ratings as QUALITY (collectability x 10), lowest first: the mission's
/// WKSMissionToDoEvalutionRefin row gives them as percentages of the maximum. null = not such a collectable.
/// </param>
internal sealed record VersusRecipe(
    string Item, int RecipeLevel, int ClassJobLevel, int Progress, int Quality, int Durability,
    int ProgressDivider, int ProgressModifier, int QualityDivider, int QualityModifier,
    ushort ConditionsFlag, bool Expert, bool CanHq, int RequiredQuality, int[]? CollectableTiers = null);

/// <summary>
/// One craft to play side by side: a recipe, a crafter, the quality the materials bring, and the
/// rotations raphael-cli v0.28.6 solved for exactly that - each side gets the one ITS plugin would ask for.
/// </summary>
/// <param name="Plan">What Hephaestus asks Raphael for (its default: no backloading).</param>
/// <param name="BackloadedPlan">What Artisan asks for by default (<c>--backload-progress</c>).</param>
/// <param name="ReliablePlan">Artisan's opt-in "Ensure 100% reliability" (<c>--adversarial --backload-progress</c>); it does not offer it on expert recipes.</param>
/// <param name="CosmicTools">The crafter holds a finished Cosmic tool (Good is worth x1.75) - a Master mission cannot be taken without one.</param>
internal sealed record VersusScenario(
    string Name, VersusRecipe Recipe, string Crafter, int Craftsmanship, int Control, int Cp, int InitialQuality,
    CraftAction[] Plan, CraftAction[] BackloadedPlan, CraftAction[]? ReliablePlan, bool CosmicTools = false)
{
    public int JobLevel => 100;

    /// <summary>The same craft in the simulator's terms; base values by the shared single-precision formula.</summary>
    public CraftParameters Parameters => new(
        Cp, Recipe.Durability, Recipe.Progress, Recipe.Quality,
        CraftParameters.ComputeBaseProgress(Craftsmanship, JobLevel, Recipe.ClassJobLevel, Recipe.ProgressDivider, Recipe.ProgressModifier),
        CraftParameters.ComputeBaseQuality(Control, JobLevel, Recipe.ClassJobLevel, Recipe.QualityDivider, Recipe.QualityModifier),
        JobLevel, ActionMask.Regular, CosmicTools);

    /// <summary>What counts as the item being worth having: the demanded quality, else a collectable's top rating, else full quality (a guaranteed HQ).</summary>
    public int TargetQuality => Recipe.RequiredQuality > 0 ? Recipe.RequiredQuality : Recipe.CollectableTiers?[^1] ?? Recipe.Quality;
}

/// <summary>
/// The crafts Hephaestus and Artisan are compared on - the current top tier, from the game's own sheets.
///
/// CRAFTER STATS ARE ASSUMED, not read from anyone's gear: "Solid" (6000 / 5700 / 680) stands for a
/// melded set with food, "Lean" (5650 / 5150 / 620) for one barely over the tier's 5620 / 5080
/// requirement. "Melded" (5911 / 5503 / 641) is REAL: the user's pentamelded level 100 set, no food
/// (2026-11-11; Teamcraft's 7.51 High Tier set is 5933 / 5470 / 649); "MeldedFood" (5911 / 5618 / 741) is the same set
/// with All i Pebre HQ. Change them here and regenerate the plans (the command is beside each) to measure a
/// real character. Neither side is a specialist, and both have Manipulation.
///
/// Every rotation is real raphael-cli v0.28.6 output (built by tools/build-raphael-cli.ps1), solved
/// 2026-11-11 with: raphael-cli solve --custom-recipe RLVL PROGRESS QUALITY DURABILITY EXPERT
/// --override-base-increases 100 BASEPROGRESS BASEQUALITY --stats C CTRL CP --level 100 --manipulation
/// [--initial-quality N] [--backload-progress] [--adversarial] --output-variables actions
/// </summary>
internal static class VersusScenarios
{
    /// <summary>A 4-star intermediate (the HQ materials the gear and potions want). No material quality: all 14,204 is made at the bench.</summary>
    public static readonly VersusRecipe Ingot770 = new(
        "Double Duraluminum Ingot", 770, 100, 5622, 14204, 35, 170, 90, 150, 75, 15, Expert: false, CanHq: true, RequiredQuality: 0);

    /// <summary>The 4-star final item - 110 recipes share these numbers (the tier's gear, potions and food). HQ materials give up to half the quality.</summary>
    public static readonly VersusRecipe Gemdraught770 = new(
        "Grade 4 Gemdraught of Strength", 770, 100, 10040, 21200, 70, 170, 90, 150, 75, 15, Expert: false, CanHq: true, RequiredQuality: 0);

    /// <summary>Expert, 20 durability; the craft FAILS below 14,900 quality.</summary>
    public static readonly VersusRecipe SurveyPlank742 = new(
        "Hardened Survey Plank", 742, 100, 4700, 14900, 20, 180, 100, 180, 100, 995, Expert: true, CanHq: false, RequiredQuality: 14900);

    /// <summary>Expert; the craft FAILS below 19,200 quality.</summary>
    public static readonly VersusRecipe Reinforcement754 = new(
        "Reinforcement Supplies II", 754, 100, 6500, 19200, 35, 180, 100, 180, 100, 435, Expert: true, CanHq: false, RequiredQuality: 19200);

    /// <summary>Expert; the craft FAILS below 23,000 quality.</summary>
    public static readonly VersusRecipe OizysRarities759 = new(
        "Oizys Rarities", 759, 100, 9000, 23000, 50, 180, 100, 180, 100, 1523, Expert: true, CanHq: false, RequiredQuality: 23000);

    /// <summary>Expert, Cosmic Master mission 1 of 3 (unit 1395 for Carpenter; the same recipe shape for all eight trades): 5 minutes, Material Miracle. A collectable rated at 50 / 60 / 85% of the quality.</summary>
    public static readonly VersusRecipe Biofuel773 = new(
        "Heavy Machinery Biofuel", 773, 100, 7800, 25100, 60, 180, 100, 180, 100, 1523, Expert: true, CanHq: true, RequiredQuality: 0, CollectableTiers: new[] { 12550, 15060, 21330 });

    /// <summary>Expert, Cosmic Master mission 2 of 3 (unit 1396): 4 minutes, Stellar Steady Hand. A collectable rated at 50 / 60 / 85%.</summary>
    public static readonly VersusRecipe Compounds774 = new(
        "Regrowth Formula Compounds", 774, 100, 6900, 22100, 45, 170, 90, 150, 75, 995, Expert: true, CanHq: true, RequiredQuality: 0, CollectableTiers: new[] { 11050, 13260, 18780 });

    /// <summary>Expert, Cosmic Master mission 3 of 3 (unit 1397, Master: Renewable Fuel Survey): 7 minutes, Material Miracle. Rated at 50 / 60 / 85% of the quality (collectability 1315 / 1578 / 2235 in the user's screenshots, 2026-11-11). Gold is the goal.</summary>
    public static readonly VersusRecipe Biocoke775 = new(
        "Auxesian Biocoke", 775, 100, 7400, 26300, 80, 170, 90, 150, 75, 515, Expert: true, CanHq: true, RequiredQuality: 0, CollectableTiers: new[] { 13150, 15780, 22350 });

    /// <summary>Expert; the craft FAILS below 31,500 quality (user's trial synthesis, 2026-11-11, ended at 9,406). Raphael's best all-Normal rotation reaches 12,150 at the Melded stats.</summary>
    public static readonly VersusRecipe AqueductFragment776 = new(
        "Crumbling Aqueduct Fragment", 776, 100, 11250, 31520, 60, 180, 100, 180, 100, 1523, Expert: true, CanHq: false, RequiredQuality: 31500);

    public static readonly VersusScenario[] All =
    {
        // On paper (all Normal): plan 14274, backloaded 14304, reliable 12976 of 14204.
        new("Ingot770-Solid", Ingot770, "Solid", 6000, 5700, 680, InitialQuality: 0,
            Plan: new[]
            {
                MuscleMemory, Manipulation, Veneration, WasteNot2, Groundwork, DelicateSynthesis, DelicateSynthesis, Innovation,
                PreparatoryTouch, PreparatoryTouch, GreatStrides, PreparatoryTouch, TrainedPerfection, GreatStrides, Innovation, PreparatoryTouch,
                TrainedFinesse, GreatStrides, ByregotsBlessing, DelicateSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Manipulation, BasicTouch, RefinedTouch, GreatStrides, Innovation, PreparatoryTouch, TrainedPerfection,
                GreatStrides, PreparatoryTouch, GreatStrides, Manipulation, Innovation, PreparatoryTouch, GreatStrides, ByregotsBlessing,
                WasteNot, Veneration, Groundwork, Groundwork, Groundwork, BasicSynthesis,
            },
            ReliablePlan: new[]
            {
                Reflect, TrainedPerfection, Manipulation, Innovation, PreparatoryTouch, BasicTouch, StandardTouch, AdvancedTouch,
                Innovation, BasicTouch, StandardTouch, AdvancedTouch, TrainedFinesse, Manipulation, Innovation, TrainedFinesse,
                TrainedFinesse, TrainedFinesse, ByregotsBlessing, WasteNot, Veneration, Groundwork, Groundwork, Groundwork,
                BasicSynthesis,
            }),

        // On paper (all Normal): plan 12127, backloaded 12080, reliable 9941 of 14204.
        new("Ingot770-Lean", Ingot770, "Lean", 5650, 5150, 620, InitialQuality: 0,
            Plan: new[]
            {
                Reflect, Manipulation, Innovation, Veneration, DelicateSynthesis, DelicateSynthesis, DelicateSynthesis, CarefulSynthesis,
                TrainedPerfection, Veneration, Groundwork, Manipulation, Innovation, DelicateSynthesis, BasicTouch, StandardTouch,
                AdvancedTouch, GreatStrides, Innovation, Observe, AdvancedTouch, GreatStrides, ByregotsBlessing, CarefulSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Manipulation, BasicTouch, RefinedTouch, TrainedPerfection, GreatStrides, Innovation, PreparatoryTouch,
                BasicTouch, StandardTouch, AdvancedTouch, Manipulation, Innovation, Observe, AdvancedTouch, GreatStrides,
                ByregotsBlessing, WasteNot, Veneration, Groundwork, Groundwork, Groundwork, CarefulSynthesis,
            },
            ReliablePlan: new[]
            {
                Reflect, Innovation, Manipulation, PrudentTouch, PrudentTouch, PrudentTouch, Innovation, PrudentTouch,
                BasicTouch, StandardTouch, AdvancedTouch, Manipulation, Innovation, PrudentTouch, TrainedFinesse, TrainedFinesse,
                ByregotsBlessing, Veneration, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis, PrudentSynthesis, TrainedPerfection, Veneration,
                Groundwork, CarefulSynthesis,
            }),

        // On paper (all Normal): plan 21311, backloaded 21794, reliable 21336 of 21200.
        new("Gemdraught770-Solid-HqMats", Gemdraught770, "Solid", 6000, 5700, 680, InitialQuality: 10600,
            Plan: new[]
            {
                MuscleMemory, WasteNot2, Veneration, Groundwork, DelicateSynthesis, Groundwork, Groundwork, Innovation,
                PreparatoryTouch, PreparatoryTouch, ImmaculateMend, DelicateSynthesis, GreatStrides, Innovation, PreparatoryTouch, DelicateSynthesis,
                GreatStrides, ByregotsBlessing, Groundwork,
            },
            BackloadedPlan: new[]
            {
                Reflect, WasteNot, Innovation, PreparatoryTouch, PreparatoryTouch, PreparatoryTouch, PreparatoryTouch, ImmaculateMend,
                GreatStrides, Innovation, ByregotsBlessing, WasteNot2, Veneration, Groundwork, Groundwork, Groundwork,
                Groundwork, Veneration, Groundwork, Groundwork,
            },
            ReliablePlan: new[]
            {
                Reflect, Manipulation, Innovation, PrudentTouch, PreparatoryTouch, PreparatoryTouch, AdvancedTouch, Innovation,
                BasicTouch, StandardTouch, AdvancedTouch, ByregotsBlessing, ImmaculateMend, WasteNot, Veneration, Groundwork,
                Groundwork, Groundwork, Groundwork, TrainedPerfection, Veneration, Groundwork, Groundwork,
            }),

        // On paper (all Normal): plan 21407, backloaded 21407, reliable 20321 of 21200.
        new("Gemdraught770-Lean-HqMats", Gemdraught770, "Lean", 5650, 5150, 620, InitialQuality: 10600,
            Plan: new[]
            {
                Reflect, BasicTouch, RefinedTouch, TrainedPerfection, GreatStrides, Innovation, PreparatoryTouch, BasicTouch,
                StandardTouch, AdvancedTouch, ImmaculateMend, Innovation, GreatStrides, Veneration, WasteNot2, ByregotsBlessing,
                Groundwork, Groundwork, Veneration, Groundwork, Groundwork, Groundwork, Groundwork, BasicSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, BasicTouch, RefinedTouch, TrainedPerfection, GreatStrides, Innovation, PreparatoryTouch, BasicTouch,
                StandardTouch, AdvancedTouch, ImmaculateMend, Innovation, GreatStrides, Veneration, WasteNot2, ByregotsBlessing,
                Groundwork, Groundwork, Veneration, Groundwork, Groundwork, Groundwork, Groundwork, BasicSynthesis,
            },
            ReliablePlan: new[]
            {
                Reflect, BasicTouch, StandardTouch, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch,
                ImmaculateMend, Innovation, BasicTouch, StandardTouch, AdvancedTouch, ByregotsBlessing, TrainedPerfection, Veneration,
                Groundwork, CarefulSynthesis, CarefulSynthesis, PrudentSynthesis, ImmaculateMend, Veneration, Groundwork, Groundwork,
                Groundwork, PrudentSynthesis, BasicSynthesis,
            }),

        // On paper (all Normal): plan 14644, backloaded 14364, reliable 12424 of 21200.
        new("Gemdraught770-Solid-NqMats", Gemdraught770, "Solid", 6000, 5700, 680, InitialQuality: 0,
            Plan: new[]
            {
                Reflect, Veneration, Groundwork, Groundwork, CarefulSynthesis, PrudentSynthesis, ImmaculateMend, TrainedPerfection,
                Veneration, Groundwork, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis, Innovation, PrudentTouch, BasicTouch,
                StandardTouch, AdvancedTouch, ImmaculateMend, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch,
                Innovation, BasicTouch, StandardTouch, GreatStrides, ByregotsBlessing, CarefulSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Innovation, PrudentTouch, PrudentTouch, PrudentTouch, PrudentTouch, Innovation, PrudentTouch,
                BasicTouch, StandardTouch, AdvancedTouch, ImmaculateMend, GreatStrides, Innovation, Observe, AdvancedTouch,
                GreatStrides, ByregotsBlessing, Veneration, WasteNot, Groundwork, Groundwork, Groundwork, CarefulSynthesis,
                TrainedPerfection, Veneration, Groundwork, PrudentSynthesis, PrudentSynthesis, CarefulSynthesis,
            },
            ReliablePlan: new[]
            {
                Reflect, PrudentTouch, PrudentTouch, Observe, AdvancedTouch, Innovation, PrudentTouch, BasicTouch,
                StandardTouch, AdvancedTouch, ImmaculateMend, Observe, AdvancedTouch, Innovation, BasicTouch, StandardTouch,
                AdvancedTouch, ByregotsBlessing, TrainedPerfection, Veneration, Groundwork, CarefulSynthesis, PrudentSynthesis, ImmaculateMend,
                Veneration, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis, CarefulSynthesis, Veneration, CarefulSynthesis, CarefulSynthesis,
                PrudentSynthesis, BasicSynthesis,
            }),

        // On paper (all Normal): plan 13675, backloaded 13576, reliable 11463 of 14204.
        new("Ingot770-Melded", Ingot770, "Melded", 5911, 5503, 641, InitialQuality: 0,
            Plan: new[]
            {
                Reflect, Manipulation, Innovation, Veneration, DelicateSynthesis, DelicateSynthesis, DelicateSynthesis, DelicateSynthesis,
                TrainedPerfection, Veneration, Groundwork, Manipulation, Innovation, DelicateSynthesis, BasicTouch, StandardTouch,
                AdvancedTouch, GreatStrides, Innovation, Observe, AdvancedTouch, GreatStrides, ByregotsBlessing, CarefulSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, TrainedPerfection, Manipulation, Innovation, PreparatoryTouch, BasicTouch, StandardTouch, AdvancedTouch,
                Innovation, Observe, AdvancedTouch, Observe, AdvancedTouch, Manipulation, GreatStrides, Innovation,
                Observe, AdvancedTouch, GreatStrides, ByregotsBlessing, WasteNot, Veneration, Groundwork, Groundwork,
                Groundwork, BasicSynthesis,
            },
            ReliablePlan: new[]
            {
                Reflect, Manipulation, Innovation, PrudentTouch, PrudentTouch, PrudentTouch, PrudentTouch, Innovation,
                PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, Manipulation, Innovation, TrainedFinesse, PrudentTouch,
                TrainedFinesse, ByregotsBlessing, TrainedPerfection, Veneration, Groundwork, Groundwork, PrudentSynthesis, PrudentSynthesis,
                CarefulSynthesis,
            }),

        // On paper (all Normal): plan 14305, backloaded 14304, reliable 13846 of 14204.
        new("Ingot770-MeldedFood", Ingot770, "MeldedFood", 5911, 5618, 741, InitialQuality: 0,
            Plan: new[]
            {
                MuscleMemory, Manipulation, Veneration, WasteNot2, Groundwork, Groundwork, Innovation, PreparatoryTouch,
                PreparatoryTouch, PreparatoryTouch, PreparatoryTouch, MasterMend, TrainedPerfection, GreatStrides, Innovation, PreparatoryTouch,
                PreparatoryTouch, GreatStrides, ByregotsBlessing, DelicateSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Manipulation, BasicTouch, TrainedPerfection, Innovation, PreparatoryTouch, PreparatoryTouch, GreatStrides,
                PreparatoryTouch, Manipulation, GreatStrides, Innovation, WasteNot2, PreparatoryTouch, GreatStrides, ByregotsBlessing,
                Veneration, DelicateSynthesis, Groundwork, Groundwork, Groundwork,
            },
            ReliablePlan: new[]
            {
                Reflect, PrudentTouch, Manipulation, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch,
                TrainedPerfection, Innovation, BasicTouch, StandardTouch, AdvancedTouch, TrainedFinesse, Innovation, Manipulation,
                TrainedFinesse, Innovation, TrainedFinesse, TrainedFinesse, TrainedFinesse, ByregotsBlessing, WasteNot, Veneration,
                Groundwork, Groundwork, Groundwork, BasicSynthesis,
            }),

        // On paper (all Normal): plan 21328, backloaded 21314, reliable 21566 of 21200.
        new("Gemdraught770-Melded-HqMats", Gemdraught770, "Melded", 5911, 5503, 641, InitialQuality: 10600,
            Plan: new[]
            {
                MuscleMemory, WasteNot2, Veneration, Groundwork, DelicateSynthesis, Innovation, DelicateSynthesis, PreparatoryTouch,
                PreparatoryTouch, PreparatoryTouch, ImmaculateMend, Innovation, PreparatoryTouch, GreatStrides, ByregotsBlessing, TrainedPerfection,
                Veneration, Groundwork, Groundwork, Groundwork,
            },
            BackloadedPlan: new[]
            {
                Reflect, Innovation, PreparatoryTouch, PreparatoryTouch, StandardTouch, ImmaculateMend, TrainedPerfection, GreatStrides,
                Innovation, PreparatoryTouch, GreatStrides, ByregotsBlessing, WasteNot2, Veneration, Groundwork, Groundwork,
                Groundwork, Groundwork, Veneration, Groundwork, Groundwork,
            },
            ReliablePlan: new[]
            {
                Reflect, PrudentTouch, Innovation, BasicTouch, StandardTouch, AdvancedTouch, PreparatoryTouch, ImmaculateMend,
                PrudentTouch, Innovation, BasicTouch, StandardTouch, AdvancedTouch, ByregotsBlessing, Veneration, Groundwork,
                ImmaculateMend, Groundwork, Groundwork, TrainedPerfection, Veneration, Groundwork, Groundwork, PrudentSynthesis,
                CarefulSynthesis,
            }),

        // On paper (all Normal): plan 21220, backloaded 21834, reliable 21220 of 21200.
        new("Gemdraught770-MeldedFood-HqMats", Gemdraught770, "MeldedFood", 5911, 5618, 741, InitialQuality: 10600,
            Plan: new[]
            {
                MuscleMemory, WasteNot2, PreparatoryTouch, Veneration, Groundwork, Groundwork, Groundwork, Groundwork,
                DelicateSynthesis, ImmaculateMend, PreparatoryTouch, WasteNot2, PreparatoryTouch, Innovation, PreparatoryTouch, PreparatoryTouch,
                GreatStrides, ByregotsBlessing, Groundwork,
            },
            BackloadedPlan: new[]
            {
                Reflect, Manipulation, PreparatoryTouch, PreparatoryTouch, GreatStrides, Innovation, PreparatoryTouch, PreparatoryTouch,
                GreatStrides, ByregotsBlessing, ImmaculateMend, WasteNot2, Veneration, Groundwork, Groundwork, Groundwork,
                Groundwork, Veneration, Groundwork, Groundwork,
            },
            ReliablePlan: new[]
            {
                Reflect, Innovation, WasteNot2, PreparatoryTouch, GreatStrides, PreparatoryTouch, Innovation, PreparatoryTouch,
                PreparatoryTouch, PreparatoryTouch, ByregotsBlessing, ImmaculateMend, WasteNot, Veneration, Groundwork, Groundwork,
                Groundwork, Groundwork, TrainedPerfection, Veneration, Groundwork, Groundwork,
            }),

        // On paper (all Normal): plan 12627, backloaded 12606, reliable 11106 of 21200.
        new("Gemdraught770-Melded-NqMats", Gemdraught770, "Melded", 5911, 5503, 641, InitialQuality: 0,
            Plan: new[]
            {
                MuscleMemory, TrainedPerfection, Veneration, Groundwork, DelicateSynthesis, DelicateSynthesis, PrudentSynthesis, Veneration,
                Innovation, DelicateSynthesis, DelicateSynthesis, DelicateSynthesis, ImmaculateMend, Veneration, PrudentSynthesis, Innovation,
                DelicateSynthesis, BasicTouch, StandardTouch, AdvancedTouch, GreatStrides, Innovation, Observe, AdvancedTouch,
                GreatStrides, ByregotsBlessing, BasicSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, BasicTouch, RefinedTouch, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch,
                TrainedPerfection, ImmaculateMend, GreatStrides, Innovation, PreparatoryTouch, GreatStrides, ByregotsBlessing, WasteNot2,
                Veneration, Groundwork, Groundwork, Groundwork, Groundwork, Veneration, Groundwork, CarefulSynthesis,
                CarefulSynthesis,
            },
            ReliablePlan: new[]
            {
                Reflect, BasicTouch, RefinedTouch, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch,
                ImmaculateMend, PrudentTouch, Innovation, BasicTouch, StandardTouch, AdvancedTouch, ByregotsBlessing, Veneration,
                Groundwork, ImmaculateMend, Groundwork, Groundwork, TrainedPerfection, Veneration, Groundwork, Groundwork,
                PrudentSynthesis, CarefulSynthesis,
            }),

        // On paper (all Normal): plan 15984, backloaded 15653, reliable 13372 of 21200.
        new("Gemdraught770-MeldedFood-NqMats", Gemdraught770, "MeldedFood", 5911, 5618, 741, InitialQuality: 0,
            Plan: new[]
            {
                MuscleMemory, Veneration, Groundwork, CarefulSynthesis, CarefulSynthesis, DelicateSynthesis, PrudentTouch, ImmaculateMend,
                Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, Innovation, BasicTouch, StandardTouch,
                AdvancedTouch, ImmaculateMend, Innovation, BasicTouch, StandardTouch, AdvancedTouch, Innovation, Observe,
                AdvancedTouch, GreatStrides, ByregotsBlessing, TrainedPerfection, Veneration, Groundwork, CarefulSynthesis, PrudentSynthesis,
                BasicSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, Innovation, Observe,
                AdvancedTouch, Observe, AdvancedTouch, ImmaculateMend, Innovation, Observe, AdvancedTouch, Observe,
                AdvancedTouch, GreatStrides, Innovation, Observe, AdvancedTouch, GreatStrides, ByregotsBlessing, TrainedPerfection,
                Veneration, Groundwork, CarefulSynthesis, CarefulSynthesis, PrudentSynthesis, ImmaculateMend, Veneration, Groundwork,
                Groundwork, Groundwork, CarefulSynthesis,
            },
            ReliablePlan: new[]
            {
                Reflect, Observe, AdvancedTouch, Innovation, BasicTouch, BasicTouch, StandardTouch, AdvancedTouch,
                ImmaculateMend, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, Innovation, TrainedFinesse,
                BasicTouch, StandardTouch, ByregotsBlessing, ImmaculateMend, Veneration, WasteNot, Groundwork, Groundwork,
                Groundwork, BasicSynthesis, TrainedPerfection, Veneration, Groundwork, Groundwork, CarefulSynthesis, BasicSynthesis,
            }),

        // On paper (all Normal): plan 14932, backloaded 15022 of 14900.
        new("SurveyPlank742-Solid", SurveyPlank742, "Solid", 6000, 5700, 680, InitialQuality: 0,
            Plan: new[]
            {
                Reflect, Manipulation, WasteNot2, Veneration, Groundwork, Groundwork, Innovation, DelicateSynthesis,
                PreparatoryTouch, GreatStrides, PreparatoryTouch, TrainedPerfection, GreatStrides, Manipulation, Innovation, PreparatoryTouch,
                BasicTouch, GreatStrides, ByregotsBlessing, BasicSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Manipulation, WasteNot2, Innovation, PreparatoryTouch, PreparatoryTouch, GreatStrides, PreparatoryTouch,
                GreatStrides, Innovation, PreparatoryTouch, Manipulation, GreatStrides, ByregotsBlessing, TrainedPerfection, Veneration,
                Groundwork, CarefulSynthesis, CarefulSynthesis, PrudentSynthesis, BasicSynthesis,
            },
            ReliablePlan: null),

        // On paper (all Normal): plan 16555, backloaded 16242 of 19200.
        new("Reinforcement754-Solid", Reinforcement754, "Solid", 6000, 5700, 680, InitialQuality: 0,
            Plan: new[]
            {
                Reflect, Manipulation, Innovation, DelicateSynthesis, BasicTouch, StandardTouch, AdvancedTouch, Innovation,
                Observe, AdvancedTouch, Observe, AdvancedTouch, TrainedPerfection, GreatStrides, Manipulation, Innovation,
                PreparatoryTouch, TrainedFinesse, GreatStrides, ByregotsBlessing, WasteNot, Veneration, Groundwork, Groundwork,
                Groundwork, BasicSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Manipulation, Innovation, PrudentTouch, PrudentTouch, Observe, AdvancedTouch, Innovation,
                PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, TrainedPerfection, GreatStrides, Innovation, Manipulation,
                PreparatoryTouch, GreatStrides, ByregotsBlessing, Veneration, WasteNot, Groundwork, Groundwork, Groundwork,
                Groundwork,
            },
            ReliablePlan: null),

        // On paper (all Normal): plan 15040, backloaded 14699 of 23000.
        new("OizysRarities759-Solid", OizysRarities759, "Solid", 6000, 5700, 680, InitialQuality: 0,
            Plan: new[]
            {
                Reflect, Innovation, Veneration, DelicateSynthesis, DelicateSynthesis, DelicateSynthesis, PrudentSynthesis, ImmaculateMend,
                Innovation, DelicateSynthesis, BasicTouch, StandardTouch, AdvancedTouch, Manipulation, GreatStrides, Innovation,
                Observe, AdvancedTouch, GreatStrides, ByregotsBlessing, TrainedPerfection, Veneration, Groundwork, Groundwork,
                PrudentSynthesis, CarefulSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, BasicTouch, RefinedTouch, Manipulation, Innovation, PrudentTouch, BasicTouch, StandardTouch,
                AdvancedTouch, TrainedPerfection, GreatStrides, Innovation, PreparatoryTouch, GreatStrides, ByregotsBlessing, Manipulation,
                WasteNot, Veneration, Groundwork, Groundwork, Groundwork, PrudentSynthesis, Veneration, CarefulSynthesis,
                PrudentSynthesis, CarefulSynthesis,
            },
            ReliablePlan: null),

        // On paper (all Normal): plan 15754, backloaded 15375 of 25100.
        new("Biofuel773-Melded", Biofuel773, "Melded", 5911, 5503, 641, InitialQuality: 0,
            Plan: new[]
            {
                MuscleMemory, Veneration, WasteNot, Groundwork, Groundwork, Groundwork, BasicTouch, RefinedTouch,
                ImmaculateMend, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, TrainedPerfection, GreatStrides,
                Innovation, PreparatoryTouch, Observe, AdvancedTouch, TrainedFinesse, Innovation, TrainedFinesse, GreatStrides,
                ByregotsBlessing, BasicSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Innovation, BasicTouch, RefinedTouch, Observe, AdvancedTouch, Manipulation, Innovation,
                PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, GreatStrides, Innovation, Observe, AdvancedTouch,
                GreatStrides, ByregotsBlessing, TrainedPerfection, ImmaculateMend, Veneration, Groundwork, CarefulSynthesis, CarefulSynthesis,
                CarefulSynthesis, Veneration, CarefulSynthesis, CarefulSynthesis, PrudentSynthesis, CarefulSynthesis,
            },
            ReliablePlan: null, CosmicTools: true),

        // On paper (all Normal): plan 13169, backloaded 12604 of 22100.
        new("Compounds774-Melded", Compounds774, "Melded", 5911, 5503, 641, InitialQuality: 0,
            Plan: new[]
            {
                MuscleMemory, TrainedPerfection, Manipulation, Veneration, Groundwork, DelicateSynthesis, DelicateSynthesis, DelicateSynthesis,
                Innovation, DelicateSynthesis, BasicTouch, StandardTouch, AdvancedTouch, Manipulation, Innovation, Observe,
                AdvancedTouch, Observe, AdvancedTouch, GreatStrides, Innovation, Observe, AdvancedTouch, GreatStrides,
                ByregotsBlessing, CarefulSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Manipulation, Innovation, BasicTouch, RefinedTouch, Observe, AdvancedTouch, Innovation,
                BasicTouch, StandardTouch, AdvancedTouch, Manipulation, GreatStrides, Innovation, Observe, AdvancedTouch,
                GreatStrides, ByregotsBlessing, TrainedPerfection, Groundwork, WasteNot, Veneration, Groundwork, Groundwork,
                Groundwork, CarefulSynthesis,
            },
            ReliablePlan: null, CosmicTools: true),

        // On paper (all Normal): plan 16203, backloaded 15954 of 26300.
        new("Biocoke775-Melded", Biocoke775, "Melded", 5911, 5503, 641, InitialQuality: 0,
            Plan: new[]
            {
                Reflect, Veneration, Groundwork, PrudentSynthesis, Innovation, DelicateSynthesis, BasicTouch, StandardTouch,
                AdvancedTouch, ImmaculateMend, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, GreatStrides,
                Observe, AdvancedTouch, GreatStrides, Innovation, Observe, AdvancedTouch, GreatStrides, ByregotsBlessing,
                TrainedPerfection, Veneration, Groundwork, PrudentSynthesis, PrudentSynthesis, CarefulSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, BasicTouch, RefinedTouch, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch,
                GreatStrides, Innovation, Observe, AdvancedTouch, TrainedFinesse, TrainedFinesse, ImmaculateMend, GreatStrides,
                Innovation, Observe, AdvancedTouch, GreatStrides, ByregotsBlessing, TrainedPerfection, Veneration, Groundwork,
                CarefulSynthesis, CarefulSynthesis, CarefulSynthesis, Veneration, CarefulSynthesis, CarefulSynthesis, PrudentSynthesis, CarefulSynthesis,
            },
            ReliablePlan: null, CosmicTools: true),

        // On paper (all Normal): plan 12150, backloaded 11730 of 31520.
        new("AqueductFragment776-Melded", AqueductFragment776, "Melded", 5911, 5503, 641, InitialQuality: 0,
            Plan: new[]
            {
                MuscleMemory, Veneration, Groundwork, DelicateSynthesis, CarefulSynthesis, PrudentSynthesis, ImmaculateMend, BasicTouch,
                RefinedTouch, Innovation, PrudentTouch, BasicTouch, StandardTouch, AdvancedTouch, ImmaculateMend, TrainedPerfection,
                Veneration, Groundwork, CarefulSynthesis, CarefulSynthesis, PrudentSynthesis, Innovation, BasicTouch, StandardTouch,
                GreatStrides, ByregotsBlessing, BasicSynthesis,
            },
            BackloadedPlan: new[]
            {
                Reflect, Innovation, BasicTouch, RefinedTouch, PrudentTouch, PrudentTouch, Innovation, PrudentTouch,
                PrudentTouch, PrudentTouch, ImmaculateMend, GreatStrides, Innovation, WasteNot2, ByregotsBlessing, Veneration,
                Groundwork, Groundwork, Groundwork, Groundwork, Veneration, Groundwork, TrainedPerfection, Groundwork,
                BasicSynthesis,
            },
            ReliablePlan: null),

    };

    public static VersusScenario Named(string name) => All.Single(s => s.Name == name);
}
