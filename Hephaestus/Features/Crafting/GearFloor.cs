using System;

namespace Hephaestus.Features.Crafting;

/// <summary>
/// "The work in hand may go on under the player's gear gate, down to a floor" (Hephaestus.FinishUnderGearGate,
/// 2026-11-15, for Selene: a Cosmic mission whose gear crosses the gate between two items is finished, and the
/// repair follows before the next one - the user: "not turn in a mission if we drop to 19% during a mission,
/// just have it finish"). It only ever LOWERS the gate, never under <see cref="HardFloorPercent"/>, and belongs
/// to the work it was given for: whoever owns it drops it when that work stops. Nothing is saved.
/// </summary>
public sealed class GearFloor
{
    /// <summary>
    /// No caller goes under this. Gear keeps its stats until its condition reaches 0, so the floor only has to
    /// keep a synthesis from ENDING at 0. What one synthesis wears off a piece has NOT been measured - 5 is a
    /// cautious pick, not a figure from the game.
    /// </summary>
    public const int HardFloorPercent = 5;

    /// <summary>The floor the work in hand may go down to; 0 = none, the player's gate holds.</summary>
    public int Percent { get; private set; }

    /// <summary>What a request comes to: never above the player's gate, never under the hard floor (unless the gate itself is).</summary>
    public static int Effective(int askedPercent, int playerGatePercent)
        => Math.Min(playerGatePercent, Math.Max(askedPercent, HardFloorPercent));

    /// <summary>The gate a run checks its gear against right now.</summary>
    public int Gate(int playerGatePercent) => Percent > 0 ? Math.Min(playerGatePercent, Percent) : playerGatePercent;

    /// <summary>
    /// Take a floor for the work in hand. False, and nothing changes: there is no such work, or the gear is
    /// already under the floor this request comes to.
    /// </summary>
    public bool Request(int askedPercent, int playerGatePercent, int gearPercent, bool workInHand)
    {
        var floor = Effective(askedPercent, playerGatePercent);
        if (!workInHand || gearPercent < floor)
            return false;

        Percent = floor;
        return true;
    }

    /// <summary>The work it was given for has stopped: the player's gate holds again.</summary>
    public void Drop() => Percent = 0;
}
