namespace Hephaestus.Features.Crafting.Simulation;

/// <summary>
/// One moment of a synthesis. A plain value type on purpose: the lookahead copies millions of
/// these, so there is nothing to allocate and nothing shared to corrupt across threads.
/// Buff fields hold turns remaining (0 = inactive); Inner Quiet holds stacks.
/// </summary>
public struct CraftState
{
    public int Cp;
    public int Durability;
    public int Progress;
    public int Quality;

    public byte InnerQuiet;
    public byte WasteNot;
    public byte Innovation;
    public byte Veneration;
    public byte GreatStrides;
    public byte MuscleMemory;
    public byte Manipulation;

    public bool TrainedPerfectionAvailable;
    public bool TrainedPerfectionActive;
    public bool HeartAndSoulAvailable;
    public bool HeartAndSoulActive;
    public bool QuickInnovationAvailable;

    /// <summary>Set by a successful Hasty Touch (Lv96+); lasts one step and turns Hasty Touch into Daring Touch.</summary>
    public bool Expedience;

    /// <summary>
    /// Uses of Stellar Steady Hand left. They belong to the Cosmic MISSION, not to the craft:
    /// <see cref="CraftSimulator.Initial"/> leaves them at 0 and whoever starts the craft hands them in.
    /// </summary>
    public byte SteadyHandCharges;

    /// <summary>Steps left on which no action can fail (Stellar Steady Hand).</summary>
    public byte SteadyHand;

    /// <summary>Uses of Material Miracle left; the mission's, like <see cref="SteadyHandCharges"/>.</summary>
    public byte MaterialMiracleCharges;

    /// <summary>
    /// Material Miracle runs on the CLOCK, not on steps: quarter-seconds left (180 = the full 45 s),
    /// worn down by <see cref="CraftSimulator.QuarterSeconds"/> of every action. While it is above 0 the
    /// next condition comes from <see cref="ConditionOdds.Next(float[], bool, Condition, float, bool)"/>.
    /// </summary>
    public byte MaterialMiracle;

    public Combo Combo;
}
