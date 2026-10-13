using System.Collections.Generic;
using System.Linq;

namespace Hephaestus.Features.Crafting;

/// <summary>One copy of a piece of gear the crafting log could take as a material.</summary>
public readonly record struct GearCopy(bool Hq, bool HasMateria);

/// <summary>What the character holds of a piece of gear a recipe uses up, and what else needs it.</summary>
/// <param name="Needed">How many the recipe uses up per craft.</param>
/// <param name="Offered">The copies in the bags and the armoury chest — what the log's picker can offer.</param>
/// <param name="WornNq">NQ copies being worn: never offered, but they do satisfy a gearset.</param>
/// <param name="WornHq">The same, HQ.</param>
/// <param name="GearsetWearsNq">A saved gearset wears the NQ version.</param>
/// <param name="GearsetWearsHq">A saved gearset wears the HQ version.</param>
public sealed record GearHoldings(
    string Name, int Needed, IReadOnlyList<GearCopy> Offered,
    int WornNq = 0, int WornHq = 0, bool GearsetWearsNq = false, bool GearsetWearsHq = false);

/// <param name="Refusal">Why nothing is chosen; null when choosing is safe.</param>
/// <param name="PreferNq">The quality to take when both are offered.</param>
public readonly record struct GearChoice(string? Refusal, bool PreferNq)
{
    public bool Safe => Refusal == null;
}

/// <summary>
/// A recipe that uses up a piece of GEAR makes the crafting log ask which copy ("Unselected"): gear is
/// not a stack, a copy can carry materia, and a copy can be what a saved gearset wears. Counting is not
/// enough here — the choice destroys one particular item. Pure.
///
/// The rule is built so that it does NOT matter which offered copy the game ends up taking: the picker
/// is only used when EVERY offered copy is safe to lose. How the picker orders its entries is known from
/// other windows, not from this one, so nothing here may depend on it.
///   - a copy with materia is on offer            -> refuse (the materia would be destroyed with it)
///   - an offered quality is what a saved gearset wears, and it is the last copy of that quality
///                                                -> refuse (the gearset would come up empty-handed)
///   - more than one is used up per craft         -> refuse (the picker's behaviour there is unknown)
/// A refusal flags the item for review with the reason; a person chooses, or makes it safe.
/// Preference, once safe: NQ before HQ, as for any material a vendor or a craft can replace.
/// </summary>
public static class GearMaterial
{
    public static GearChoice Choose(GearHoldings held)
    {
        if (held.Needed != 1)
            return Refuse($"{held.Needed} × {held.Name} are used up as materials — more than one piece of gear has to be chosen by hand");

        if (held.Offered.Count == 0)
            return Refuse($"no {held.Name} in the bags or the armoury chest to use as a material (one being worn is never offered)");

        if (held.Offered.Any(copy => copy.HasMateria))
            return Refuse($"a {held.Name} with materia melded could be the one used up — choose it by hand, or take the materia off");

        var nq = held.Offered.Count(copy => !copy.Hq);
        var hq = held.Offered.Count - nq;
        if (nq > 0 && held.GearsetWearsNq && nq + held.WornNq < 2)
            return Refuse($"the only {held.Name} is what a saved gearset wears — using it up would leave that gearset without it");
        if (hq > 0 && held.GearsetWearsHq && hq + held.WornHq < 2)
            return Refuse($"the only high-quality {held.Name} is what a saved gearset wears — using it up would leave that gearset without it");

        return new GearChoice(null, PreferNq: nq > 0);
    }

    /// <summary>
    /// Which entry of the picker to take. A preference only — <see cref="Choose"/> has already made every
    /// entry safe. Where a window lists both qualities the game puts HQ first (TextAdvance's finding for the
    /// hand-in picker), so the last entry is the NQ one.
    /// </summary>
    public static int Entry(GearChoice choice, GearHoldings held, int entryCount)
    {
        var bothOffered = held.Offered.Any(copy => copy.Hq) && held.Offered.Any(copy => !copy.Hq);
        return choice.PreferNq && bothOffered && entryCount >= 2 ? entryCount - 1 : 0;
    }

    private static GearChoice Refuse(string why) => new(why, PreferNq: false);
}
