namespace Hephaestus.Features.Crafting.Simulation;

/// <summary>
/// Every crafting action the simulator models. Order is ours (not a game id) — it only indexes
/// <see cref="ActionMask"/> bits.
///
/// Not modelled yet: Final Appraisal and Careful Observation.
///
/// The two Cosmic Exploration duty actions ARE modelled but are never offered by the plugin: they
/// are outside <see cref="ActionMask.Regular"/>, and they need charges nobody but a mission gives
/// (<see cref="CraftState.SteadyHandCharges"/>, <see cref="CraftState.MaterialMiracleCharges"/>).
/// </summary>
public enum CraftAction : byte
{
    BasicSynthesis,
    BasicTouch,
    MasterMend,
    Observe,
    TricksOfTheTrade,
    WasteNot,
    Veneration,
    StandardTouch,
    GreatStrides,
    Innovation,
    WasteNot2,
    ByregotsBlessing,
    PreciseTouch,
    MuscleMemory,
    CarefulSynthesis,
    Manipulation,
    PrudentTouch,
    AdvancedTouch,
    Reflect,
    PreparatoryTouch,
    Groundwork,
    DelicateSynthesis,
    IntensiveSynthesis,
    TrainedEye,
    HeartAndSoul,
    PrudentSynthesis,
    TrainedFinesse,
    RefinedTouch,
    QuickInnovation,
    ImmaculateMend,
    TrainedPerfection,
    RapidSynthesis,
    HastyTouch,
    DaringTouch,

    /// <summary>Cosmic Exploration duty action: the next three steps cannot fail. Takes a step and a mission charge.</summary>
    StellarSteadyHand,

    /// <summary>
    /// Cosmic Exploration duty action: for 45 seconds every new condition is one of Good, Centered, Sturdy,
    /// Pliant, Malleable or Primed. Instant (no step); takes a mission charge.
    /// </summary>
    MaterialMiracle,
}

/// <summary>Why an action cannot be used from a state. <see cref="None"/> means it can.</summary>
public enum ActionError : byte
{
    None,

    /// <summary>The synthesis is over: progress is full or durability is gone.</summary>
    StateIsFinal,
    InsufficientLevel,

    /// <summary>Excluded by <see cref="CraftParameters.AllowedActions"/> (not unlocked, not a specialist, ...).</summary>
    NotAllowed,
    InsufficientCp,

    /// <summary>Once-per-synthesis action already spent.</summary>
    NoRemainingUses,

    /// <summary>Needs to follow a specific action, or to be the first step.</summary>
    ComboRequirementNotMet,

    /// <summary>Needs a condition, a buff, or the absence of one (e.g. Prudent Touch under Waste Not).</summary>
    ConditionNotMet,
}

/// <summary>What the previous action set up for the next one.</summary>
public enum Combo : byte
{
    None,

    /// <summary>No step has been taken yet: Muscle Memory, Reflect and Trained Eye are usable.</summary>
    SynthesisBegin,

    /// <summary>After Basic Touch: Standard Touch costs 18 CP, Refined Touch is usable.</summary>
    BasicTouch,

    /// <summary>After a combo'd Standard Touch, or after Observe: Advanced Touch costs 18 CP.</summary>
    StandardTouch,
}

/// <summary>
/// Action names as text (raphael-cli output, the on-disk cache). Exact names only:
/// <c>Enum.TryParse</c> would happily read "7" as the seventh action, and a rotation that
/// silently contains the wrong action is worse than one that fails to parse.
/// </summary>
public static class CraftActionNames
{
    private static readonly System.Collections.Generic.Dictionary<string, CraftAction> ByName = Build();

    public static bool TryParse(string name, out CraftAction action) => ByName.TryGetValue(name, out action);

    private static System.Collections.Generic.Dictionary<string, CraftAction> Build()
    {
        var map = new System.Collections.Generic.Dictionary<string, CraftAction>(System.StringComparer.Ordinal);
        foreach (var action in System.Enum.GetValues<CraftAction>())
            map[action.ToString()] = action;
        return map;
    }
}

/// <summary>Bit set of <see cref="CraftAction"/>s, for <see cref="CraftParameters.AllowedActions"/>.</summary>
public static class ActionMask
{
    public const ulong All = ulong.MaxValue;

    /// <summary>
    /// Everything except the actions that need something beyond job level: Trained Eye (recipe
    /// must be 10+ levels below you and not expert), the specialist actions, and the Cosmic
    /// Exploration duty actions (a mission lends them).
    /// </summary>
    public static readonly ulong Regular =
        Without(Without(Without(Without(Without(All, CraftAction.TrainedEye), CraftAction.HeartAndSoul), CraftAction.QuickInnovation),
            CraftAction.StellarSteadyHand), CraftAction.MaterialMiracle);

    public static ulong With(ulong mask, CraftAction action) => mask | (1UL << (int)action);

    public static ulong Without(ulong mask, CraftAction action) => mask & ~(1UL << (int)action);

    public static bool Has(ulong mask, CraftAction action) => (mask & (1UL << (int)action)) != 0;
}
