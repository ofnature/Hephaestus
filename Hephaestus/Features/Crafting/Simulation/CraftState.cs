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

    public Combo Combo;
}
