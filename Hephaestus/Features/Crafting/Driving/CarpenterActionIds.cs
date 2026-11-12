using System.Collections.Generic;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Driving;

/// <summary>
/// The Carpenter version of every crafting action. Every crafting job has its own copy of each
/// action under its own id; the other seven are found from these by NAME in the game's sheets
/// (see Services/Game/GameActionMap), which is also how Artisan does it. Ids of 100000 and above
/// live in the CraftAction sheet, the rest in the Action sheet.
/// </summary>
public static class CarpenterActionIds
{
    public static readonly IReadOnlyDictionary<CraftAction, uint> All = new Dictionary<CraftAction, uint>
    {
        [CraftAction.BasicSynthesis] = 100001,
        [CraftAction.BasicTouch] = 100002,
        [CraftAction.MasterMend] = 100003,
        [CraftAction.Observe] = 100010,
        [CraftAction.TricksOfTheTrade] = 100371,
        [CraftAction.WasteNot] = 4631,
        [CraftAction.Veneration] = 19297,
        [CraftAction.StandardTouch] = 100004,
        [CraftAction.GreatStrides] = 260,
        [CraftAction.Innovation] = 19004,
        [CraftAction.WasteNot2] = 4639,
        [CraftAction.ByregotsBlessing] = 100339,
        [CraftAction.PreciseTouch] = 100128,
        [CraftAction.MuscleMemory] = 100379,
        [CraftAction.CarefulSynthesis] = 100203,
        [CraftAction.Manipulation] = 4574,
        [CraftAction.PrudentTouch] = 100227,
        [CraftAction.AdvancedTouch] = 100411,
        [CraftAction.Reflect] = 100387,
        [CraftAction.PreparatoryTouch] = 100299,
        [CraftAction.Groundwork] = 100403,
        [CraftAction.DelicateSynthesis] = 100323,
        [CraftAction.IntensiveSynthesis] = 100315,
        [CraftAction.TrainedEye] = 100283,
        [CraftAction.HeartAndSoul] = 100419,
        [CraftAction.PrudentSynthesis] = 100427,
        [CraftAction.TrainedFinesse] = 100435,
        [CraftAction.RefinedTouch] = 100443,
        [CraftAction.QuickInnovation] = 100459,
        [CraftAction.ImmaculateMend] = 100467,
        [CraftAction.TrainedPerfection] = 100475,
        [CraftAction.RapidSynthesis] = 100363,
        [CraftAction.HastyTouch] = 100355,
        [CraftAction.DaringTouch] = 100451,

        // Cosmic Exploration duty actions: one row for every job, in the Action sheet. Listed so the
        // enum stays whole; the plugin never recommends them yet (CraftSetupReader masks them).
        [CraftAction.StellarSteadyHand] = 46843,
        [CraftAction.MaterialMiracle] = 41269,
    };

    /// <summary>Ids from here up are rows of the CraftAction sheet (and use ActionType.CraftAction); below, the Action sheet.</summary>
    public const uint FirstCraftActionId = 100000;
}
