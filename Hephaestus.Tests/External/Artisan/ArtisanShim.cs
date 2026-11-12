// Hand-written for Hephaestus's benchmark. NOT Artisan code: the few things the vendored solver
// sources (see NOTICE.md) expect from the rest of the Artisan plugin, reduced to plain data.
// Every value here is Artisan's own default or the game's own number - the source is beside each.
#nullable disable
#pragma warning disable

using System;
using System.Collections.Generic;
using Artisan.CraftingLogic;
using Artisan.CraftingLogic.Solvers;

namespace Artisan
{
    /// <summary>Artisan's plugin instance, as far as the solvers look at it: <c>P.Config</c>.</summary>
    internal static class P
    {
        public static readonly Configuration Config = new();
    }

    /// <summary>
    /// The settings the solvers read, with the defaults of Artisan/Configuration.cs - an Artisan
    /// nobody has configured. Read-only during a benchmark (it is shared by every thread).
    /// </summary>
    internal sealed class Configuration
    {
        public int MaxMaterialMiracles = 0;
        public int MinimumStepsBeforeMiracle = 10;
        public int StandardMMUses => MaxMaterialMiracles;
        public int StandardMMSteps => MinimumStepsBeforeMiracle;

        public int MaxPercentage = 100;
        public bool UseTricksGood = false;
        public int MaxIQPrepTouch = 10;
        public bool UseTricksExcellent = false;
        public bool UseSpecialist = false;
        public ExpertSolverSettings ExpertSolverConfig = new();
        public MacroSolverSettings MacroSolverConfig = new();
        public bool SkipMacroStepIfUnable = false;
        public bool DisableMacroArtisanRecommendation = false;
        public bool UseQualityStarter = false;
        public int SolverCollectibleMode = 3;
    }
}

namespace Artisan.CraftingLists
{
    /// <summary>MacroSolver asks whether a crafting list is running; a benchmark craft is a single craft.</summary>
    internal static class CraftingListUI
    {
        public static bool Processing => false;
    }
}

namespace Artisan.CraftingLogic
{
    /// <summary>
    /// Which solvers Artisan offers for a craft - CraftingProcessor's list and its selection loop,
    /// without the plugin around them. MacroSolver uses it to pick the solver it falls back to. The
    /// real list also holds the macro and Raphael definitions; neither can be a macro's own fallback
    /// here (the macro skips itself, and a Raphael flavour only exists once Artisan has cached a solve).
    /// </summary>
    internal static class CraftingProcessor
    {
        public static List<ISolverDefinition> SolverDefinitions = new()
        {
            new StandardSolverDefinition(),
            new ProgressOnlySolverDefinition(),
            new ExpertSolverDefinition(),
        };

        public static IEnumerable<ISolverDefinition.Desc> GetAvailableSolversForRecipe(CraftState craft, bool returnUnsupported, Type skipSolver = null)
        {
            foreach (var solver in SolverDefinitions)
            {
                if (solver.GetType() == skipSolver)
                    continue;

                foreach (var f in solver.Flavours(craft))
                {
                    if (returnUnsupported || f.UnsupportedReason.Length == 0)
                    {
                        yield return f;
                    }
                }
            }
        }
    }
}

namespace Artisan.RawInformation.Character
{
    /// <summary>
    /// What Artisan reads from the game's CraftAction / Action sheets. Levels are the sheets'
    /// ClassJobLevel, read from the 2026-11-03 client; the standard CP cost is the sheets' cost, which
    /// is exactly Simulator.GetBaseCPCost with no combo (checked against the same client).
    /// </summary>
    public static class SkillActionMap
    {
        public static int Level(this Skills skill) => skill switch
        {
            Skills.BasicSynthesis => 1,
            Skills.BasicTouch => 5,
            Skills.MastersMend => 7,
            Skills.RapidSynthesis or Skills.HastyTouch => 9,
            Skills.Observe or Skills.TricksOfTrade => 13,
            Skills.WasteNot or Skills.Veneration => 15,
            Skills.StandardTouch => 18,
            Skills.GreatStrides => 21,
            Skills.Innovation => 26,
            Skills.FinalAppraisal => 42,
            Skills.WasteNot2 => 47,
            Skills.ByregotsBlessing => 50,
            Skills.PreciseTouch => 53,
            Skills.MuscleMemory => 54,
            Skills.CarefulObservation => 55,
            Skills.CarefulSynthesis => 62,
            Skills.Manipulation => 65,
            Skills.PrudentTouch => 66,
            Skills.AdvancedTouch => 68,
            Skills.Reflect => 69,
            Skills.PreparatoryTouch => 71,
            Skills.Groundwork => 72,
            Skills.DelicateSynthesis => 76,
            Skills.IntensiveSynthesis => 78,
            Skills.TrainedEye => 80,
            Skills.HeartAndSoul => 86,
            Skills.PrudentSynthesis => 88,
            Skills.TrainedFinesse => 90,
            Skills.RefinedTouch => 92,
            Skills.DaringTouch or Skills.QuickInnovation => 96,
            Skills.ImmaculateMend => 98,
            Skills.TrainedPerfection => 100,
            Skills.MaterialMiracle or Skills.SteadyHand => 100, // Cosmic Exploration only; never offered here
            _ => 1,
        };

        public static int StandardCPCost(this Skills skill) => Simulator.GetBaseCPCost(skill, Skills.None);
    }
}
