using System;

namespace Hephaestus.Features.Crafting.Simulation;

/// <summary>
/// The crafting rules: what an action costs, what it yields, and what state follows. Pure and
/// allocation-free — it is the inner loop of the live lookahead and of the Monte Carlo harness.
///
/// Sources: integer arithmetic, potencies, costs and effect ordering follow raphael-sim
/// (KonaeAkira/raphael-rs, Apache-2.0), whose results include rotations verified in game; the
/// expert conditions and the success rates follow Artisan's simulator. Deliberately NOT here:
/// Raphael's "adversarial" and "backload progress" modes — those are solver policies, not game rules.
/// </summary>
public static class CraftSimulator
{
    public static CraftState Initial(in CraftParameters parameters) => new()
    {
        Cp = parameters.MaxCp,
        Durability = parameters.MaxDurability,
        Combo = Combo.SynthesisBegin,
        TrainedPerfectionAvailable = IsUnlocked(parameters, CraftAction.TrainedPerfection),
        HeartAndSoulAvailable = IsUnlocked(parameters, CraftAction.HeartAndSoul),
        QuickInnovationAvailable = IsUnlocked(parameters, CraftAction.QuickInnovation),
    };

    /// <summary>Progress is full (success) or durability is gone (failure) — nothing more can be done.</summary>
    public static bool IsFinal(in CraftParameters parameters, in CraftState state)
        => state.Progress >= parameters.MaxProgress || state.Durability <= 0;

    public static bool IsSuccess(in CraftParameters parameters, in CraftState state)
        => state.Progress >= parameters.MaxProgress;

    /// <summary>Chance, in percent, that the action takes effect. Only three actions can fail.</summary>
    public static int SuccessPercent(CraftAction action, Condition condition)
    {
        var rate = action switch
        {
            CraftAction.RapidSynthesis => 50,
            CraftAction.HastyTouch or CraftAction.DaringTouch => 60,
            _ => 100,
        };
        return rate < 100 && condition == Condition.Centered ? rate + 25 : rate;
    }

    /// <summary>The same, knowing the state: nothing fails while Stellar Steady Hand is up.</summary>
    public static int SuccessPercent(in CraftState state, CraftAction action, Condition condition)
        => state.SteadyHand > 0 ? 100 : SuccessPercent(action, condition);

    /// <summary>How long Material Miracle lasts once cast, in quarter-seconds (45 s).</summary>
    public const byte MaterialMiracleQuarterSeconds = 180;

    /// <summary>
    /// How long the game keeps the crafter busy with an action, in quarter-seconds - an ESTIMATE (the one
    /// Artisan uses: 2.5 s for anything that works on the item, 1.25 s for the rest). It is what wears
    /// Material Miracle down, and what a timed Cosmic mission is counted in.
    /// </summary>
    public static int QuarterSeconds(CraftAction action)
        => BaseDurabilityCost(action) > 0 || action is CraftAction.TrainedFinesse ? 10 : 5;

    /// <summary>Whether <paramref name="action"/> may be used right now, and if not, why.</summary>
    public static ActionError Check(in CraftParameters parameters, in CraftState state, CraftAction action, Condition condition)
    {
        if (parameters.JobLevel < Level(action))
            return ActionError.InsufficientLevel;
        if (!ActionMask.Has(parameters.AllowedActions, MaskAction(action)))
            return ActionError.NotAllowed;
        if (IsFinal(parameters, state))
            return ActionError.StateIsFinal;
        if (CpCost(state, action, condition) > state.Cp)
            return ActionError.InsufficientCp;

        var goodOrBetter = condition is Condition.Good or Condition.Excellent || state.HeartAndSoulActive;
        if (NeedsGoodCondition(action))
            return goodOrBetter ? ActionError.None : ActionError.ConditionNotMet;

        return action switch
        {
            CraftAction.MuscleMemory or CraftAction.Reflect or CraftAction.TrainedEye
                => state.Combo == Combo.SynthesisBegin ? ActionError.None : ActionError.ComboRequirementNotMet,
            CraftAction.RefinedTouch
                => state.Combo == Combo.BasicTouch ? ActionError.None : ActionError.ComboRequirementNotMet,
            CraftAction.ByregotsBlessing
                => state.InnerQuiet > 0 ? ActionError.None : ActionError.ConditionNotMet,
            CraftAction.TrainedFinesse
                => state.InnerQuiet >= 10 ? ActionError.None : ActionError.ConditionNotMet,
            CraftAction.PrudentTouch or CraftAction.PrudentSynthesis
                => state.WasteNot == 0 ? ActionError.None : ActionError.ConditionNotMet,
            CraftAction.HastyTouch
                => state.Expedience ? ActionError.ConditionNotMet : ActionError.None, // it IS Daring Touch right now
            CraftAction.DaringTouch
                => state.Expedience ? ActionError.None : ActionError.ConditionNotMet,
            CraftAction.HeartAndSoul
                => state.HeartAndSoulAvailable ? ActionError.None : ActionError.NoRemainingUses,
            CraftAction.TrainedPerfection
                => state.TrainedPerfectionAvailable ? ActionError.None : ActionError.NoRemainingUses,
            CraftAction.QuickInnovation
                => !state.QuickInnovationAvailable ? ActionError.NoRemainingUses
                    : state.Innovation != 0 ? ActionError.ConditionNotMet
                    : ActionError.None,
            CraftAction.StellarSteadyHand
                => state.SteadyHandCharges > 0 ? ActionError.None : ActionError.NoRemainingUses,
            CraftAction.MaterialMiracle
                => state.MaterialMiracleCharges == 0 ? ActionError.NoRemainingUses
                    : state.MaterialMiracle != 0 ? ActionError.ConditionNotMet
                    : ActionError.None,
            _ => ActionError.None,
        };
    }

    /// <summary>
    /// Use an action. <paramref name="condition"/> is the condition the step is taken UNDER (it
    /// scales this action); the condition that follows is not the simulator's business — see
    /// <see cref="ConditionOdds"/>.
    /// </summary>
    /// <param name="success">
    /// Outcome of the success roll; only matters when <see cref="SuccessPercent"/> is below 100.
    /// A failed action still costs its CP and durability and still spends the step.
    /// </param>
    public static ActionError Use(
        in CraftParameters parameters, in CraftState state, CraftAction action, Condition condition, bool success, out CraftState next)
    {
        next = state;
        var error = Check(parameters, state, action, condition);
        if (error != ActionError.None)
            return error;

        if (state.SteadyHand > 0)
            success = true; // Stellar Steady Hand: whatever the dice said

        next.Cp -= CpCost(state, action, condition);
        next.Durability = Math.Max(0, state.Durability - DurabilityCost(state, action, condition));

        // Material Miracle is on the clock: every action, final or not, takes its time off it.
        next.MaterialMiracle = (byte)Math.Max(0, state.MaterialMiracle - QuarterSeconds(action));

        var progressGain = success ? ProgressGain(parameters, state, action, condition) : 0;
        var qualityGain = success ? QualityGain(parameters, state, action, condition) : 0;
        next.Progress = Math.Min(parameters.MaxProgress, state.Progress + progressGain);
        next.Quality = Math.Min(parameters.MaxQuality, state.Quality + qualityGain);

        if (qualityGain > 0 && parameters.JobLevel >= 11)
        {
            var stacks = action is CraftAction.PreciseTouch or CraftAction.Reflect
                or CraftAction.PreparatoryTouch or CraftAction.RefinedTouch ? 2 : 1;
            next.InnerQuiet = (byte)Math.Min(10, state.InnerQuiet + stacks);
        }

        if (IsFinal(parameters, next))
            return ActionError.None;

        // What the action spends. Great Strides and Muscle Memory are only consumed by an action
        // that actually landed; Trained Perfection is spent by anything that costs durability.
        if (qualityGain > 0)
            next.GreatStrides = 0;
        if (progressGain > 0)
            next.MuscleMemory = 0;
        if (BaseDurabilityCost(action) > 0)
            next.TrainedPerfectionActive = false;
        if (action == CraftAction.ByregotsBlessing)
            next.InnerQuiet = 0;
        if (NeedsGoodCondition(action) && condition is not (Condition.Good or Condition.Excellent))
            next.HeartAndSoulActive = false;
        if (action == CraftAction.Manipulation)
            next.Manipulation = 0; // recasting drops the old buff first, so there is no restore this turn

        var wasBeginning = state.Combo == Combo.SynthesisBegin;
        var previousCombo = state.Combo;
        next.Combo = Combo.None;

        if (SpendsStep(action))
        {
            // Manipulation restores before it ticks, and never on the turn it was cast.
            if (next.Manipulation > 0)
                next.Durability = Math.Min(parameters.MaxDurability, next.Durability + 5);
            TickDown(ref next);
        }
        else if (wasBeginning)
        {
            // "First step" is really a step count, which these actions do not advance.
            next.Combo = Combo.SynthesisBegin;
        }

        var bonus = condition == Condition.Primed ? 2 : 0;
        switch (action)
        {
            case CraftAction.MasterMend:
                next.Durability = Math.Min(parameters.MaxDurability, next.Durability + 30);
                break;
            case CraftAction.ImmaculateMend:
                next.Durability = parameters.MaxDurability;
                break;
            case CraftAction.TricksOfTheTrade:
                next.Cp = Math.Min(parameters.MaxCp, next.Cp + 20);
                break;
            case CraftAction.WasteNot:
                next.WasteNot = (byte)(4 + bonus);
                break;
            case CraftAction.WasteNot2:
                next.WasteNot = (byte)(8 + bonus);
                break;
            case CraftAction.Veneration:
                next.Veneration = (byte)(4 + bonus);
                break;
            case CraftAction.Innovation:
                next.Innovation = (byte)(4 + bonus);
                break;
            case CraftAction.QuickInnovation:
                next.Innovation = (byte)(1 + bonus);
                next.QuickInnovationAvailable = false;
                break;
            case CraftAction.GreatStrides:
                next.GreatStrides = (byte)(3 + bonus);
                break;
            case CraftAction.Manipulation:
                next.Manipulation = (byte)(8 + bonus);
                break;
            case CraftAction.MuscleMemory:
                next.MuscleMemory = (byte)(5 + bonus);
                break;
            case CraftAction.TrainedPerfection:
                next.TrainedPerfectionAvailable = false;
                next.TrainedPerfectionActive = true;
                break;
            case CraftAction.HeartAndSoul:
                next.HeartAndSoulAvailable = false;
                next.HeartAndSoulActive = true;
                break;
            case CraftAction.StellarSteadyHand:
                next.SteadyHandCharges--;
                next.SteadyHand = 3;
                break;
            case CraftAction.MaterialMiracle:
                next.MaterialMiracleCharges--;
                next.MaterialMiracle = MaterialMiracleQuarterSeconds;
                break;
            case CraftAction.BasicTouch:
                next.Combo = Combo.BasicTouch;
                break;
            case CraftAction.StandardTouch when previousCombo == Combo.BasicTouch:
            case CraftAction.Observe:
                next.Combo = Combo.StandardTouch;
                break;
            case CraftAction.HastyTouch when success && parameters.JobLevel >= Level(CraftAction.DaringTouch):
                next.Expedience = true;
                break;
        }

        return ActionError.None;
    }

    public static int CpCost(in CraftState state, CraftAction action, Condition condition)
    {
        var cost = action switch
        {
            CraftAction.BasicTouch => 18,
            CraftAction.MasterMend => 88,
            CraftAction.Observe => 7,
            CraftAction.WasteNot => 56,
            CraftAction.Veneration => 18,
            CraftAction.StandardTouch => state.Combo == Combo.BasicTouch ? 18 : 32,
            CraftAction.GreatStrides => 32,
            CraftAction.Innovation => 18,
            CraftAction.WasteNot2 => 98,
            CraftAction.ByregotsBlessing => 24,
            CraftAction.PreciseTouch => 18,
            CraftAction.MuscleMemory => 6,
            CraftAction.CarefulSynthesis => 7,
            CraftAction.Manipulation => 96,
            CraftAction.PrudentTouch => 25,
            CraftAction.AdvancedTouch => state.Combo == Combo.StandardTouch ? 18 : 46,
            CraftAction.Reflect => 6,
            CraftAction.PreparatoryTouch => 40,
            CraftAction.Groundwork => 18,
            CraftAction.DelicateSynthesis => 32,
            CraftAction.IntensiveSynthesis => 6,
            CraftAction.TrainedEye => 250,
            CraftAction.PrudentSynthesis => 18,
            CraftAction.TrainedFinesse => 32,
            CraftAction.RefinedTouch => 24,
            CraftAction.ImmaculateMend => 112,
            _ => 0,
        };
        return condition == Condition.Pliant ? cost - cost / 2 : cost; // halved, rounded up
    }

    public static int DurabilityCost(in CraftState state, CraftAction action, Condition condition)
    {
        if (state.TrainedPerfectionActive)
            return 0;

        var cost = BaseDurabilityCost(action);
        if (state.WasteNot > 0)
            cost -= cost / 2; // halved, rounded up
        if (condition is Condition.Sturdy or Condition.Robust)
            cost -= cost / 2;
        return cost;
    }

    public static int ProgressGain(in CraftParameters parameters, in CraftState state, CraftAction action, Condition condition)
    {
        var level = parameters.JobLevel;
        var potency = action switch
        {
            CraftAction.BasicSynthesis => level < 31 ? 100 : 120,
            CraftAction.CarefulSynthesis => level < 82 ? 150 : 180,
            CraftAction.RapidSynthesis => level < 63 ? 250 : 500,
            CraftAction.MuscleMemory => 300,
            CraftAction.IntensiveSynthesis => 400,
            CraftAction.PrudentSynthesis => 180,
            CraftAction.DelicateSynthesis => level < 94 ? 100 : 150,
            // Groundwork only works at half strength when there is not enough durability left for it.
            CraftAction.Groundwork => (level < 86 ? 300 : 360)
                                      / (DurabilityCost(state, action, condition) > state.Durability ? 2 : 1),
            _ => 0,
        };
        if (potency == 0)
            return 0;

        var buffs = 10 + (state.MuscleMemory > 0 ? 10 : 0) + (state.Veneration > 0 ? 5 : 0);
        var gain = (long)parameters.BaseProgress * potency * buffs;
        return (int)(condition == Condition.Malleable ? gain * 3 / 2000 : gain / 1000);
    }

    public static int QualityGain(in CraftParameters parameters, in CraftState state, CraftAction action, Condition condition)
    {
        if (action == CraftAction.TrainedEye)
            return Math.Max(0, parameters.MaxQuality - state.Quality);

        var potency = action switch
        {
            CraftAction.BasicTouch => 100,
            CraftAction.StandardTouch => 125,
            CraftAction.AdvancedTouch => 150,
            CraftAction.PrudentTouch => 100,
            CraftAction.PreparatoryTouch => 200,
            CraftAction.PreciseTouch => 150,
            CraftAction.HastyTouch => 100,
            CraftAction.DaringTouch => 150,
            CraftAction.Reflect => 300,
            CraftAction.TrainedFinesse => 100,
            CraftAction.RefinedTouch => 100,
            CraftAction.DelicateSynthesis => 100,
            CraftAction.ByregotsBlessing => 100 + 20 * state.InnerQuiet,
            _ => 0,
        };
        if (potency == 0)
            return 0;

        var buffs = (state.InnerQuiet + 10) * (10 + (state.GreatStrides > 0 ? 10 : 0) + (state.Innovation > 0 ? 5 : 0));
        var (numerator, denominator) = condition switch
        {
            Condition.Good => parameters.SplendorousTools ? (7, 4) : (3, 2),
            Condition.Excellent => (4, 1),
            Condition.Poor => (1, 2),
            _ => (1, 1),
        };
        return (int)((long)parameters.BaseQuality * potency * buffs * numerator / (10000L * denominator));
    }

    public static int Level(CraftAction action) => action switch
    {
        CraftAction.BasicSynthesis => 1,
        CraftAction.BasicTouch => 5,
        CraftAction.MasterMend => 7,
        CraftAction.RapidSynthesis or CraftAction.HastyTouch => 9,
        CraftAction.Observe or CraftAction.TricksOfTheTrade => 13,
        CraftAction.WasteNot or CraftAction.Veneration => 15,
        CraftAction.StandardTouch => 18,
        CraftAction.GreatStrides => 21,
        CraftAction.Innovation => 26,
        CraftAction.WasteNot2 => 47,
        CraftAction.ByregotsBlessing => 50,
        CraftAction.PreciseTouch => 53,
        CraftAction.MuscleMemory => 54,
        CraftAction.CarefulSynthesis => 62,
        CraftAction.Manipulation => 65,
        CraftAction.PrudentTouch => 66,
        CraftAction.AdvancedTouch => 68,
        CraftAction.Reflect => 69,
        CraftAction.PreparatoryTouch => 71,
        CraftAction.Groundwork => 72,
        CraftAction.DelicateSynthesis => 76,
        CraftAction.IntensiveSynthesis => 78,
        CraftAction.TrainedEye => 80,
        CraftAction.HeartAndSoul => 86,
        CraftAction.PrudentSynthesis => 88,
        CraftAction.TrainedFinesse => 90,
        CraftAction.RefinedTouch => 92,
        CraftAction.DaringTouch or CraftAction.QuickInnovation => 96,
        CraftAction.ImmaculateMend => 98,
        CraftAction.TrainedPerfection => 100,
        _ => 1,
    };

    /// <summary>Heart and Soul, Quick Innovation and Material Miracle are instant: no step, no buff ticks, no new condition.</summary>
    public static bool SpendsStep(CraftAction action)
        => action is not (CraftAction.HeartAndSoul or CraftAction.QuickInnovation or CraftAction.MaterialMiracle);

    private static int BaseDurabilityCost(CraftAction action) => action switch
    {
        CraftAction.BasicSynthesis or CraftAction.CarefulSynthesis or CraftAction.RapidSynthesis
            or CraftAction.IntensiveSynthesis or CraftAction.MuscleMemory or CraftAction.DelicateSynthesis
            or CraftAction.BasicTouch or CraftAction.StandardTouch or CraftAction.AdvancedTouch
            or CraftAction.HastyTouch or CraftAction.DaringTouch or CraftAction.PreciseTouch
            or CraftAction.Reflect or CraftAction.RefinedTouch or CraftAction.ByregotsBlessing
            or CraftAction.TrainedEye => 10,
        CraftAction.Groundwork or CraftAction.PreparatoryTouch => 20,
        CraftAction.PrudentSynthesis or CraftAction.PrudentTouch => 5,
        _ => 0,
    };

    /// <summary>Usable only under Good/Excellent — or any time while Heart and Soul is active, which it then spends.</summary>
    private static bool NeedsGoodCondition(CraftAction action)
        => action is CraftAction.TricksOfTheTrade or CraftAction.PreciseTouch or CraftAction.IntensiveSynthesis;

    /// <summary>Daring Touch is Hasty Touch upgraded in place, so it is allowed wherever Hasty Touch is.</summary>
    private static CraftAction MaskAction(CraftAction action)
        => action == CraftAction.DaringTouch ? CraftAction.HastyTouch : action;

    private static bool IsUnlocked(in CraftParameters parameters, CraftAction action)
        => parameters.JobLevel >= Level(action) && ActionMask.Has(parameters.AllowedActions, action);

    private static void TickDown(ref CraftState state)
    {
        if (state.WasteNot > 0) state.WasteNot--;
        if (state.Innovation > 0) state.Innovation--;
        if (state.Veneration > 0) state.Veneration--;
        if (state.GreatStrides > 0) state.GreatStrides--;
        if (state.MuscleMemory > 0) state.MuscleMemory--;
        if (state.Manipulation > 0) state.Manipulation--;
        if (state.SteadyHand > 0) state.SteadyHand--;
        state.Expedience = false;
    }
}
