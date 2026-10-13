namespace Hephaestus.Features.Crafting;

/// <summary>
/// Picks the NQ/HQ mix for one ingredient of one craft. Pure.
///
/// Rule (docs/scoping-decisions.md, "HQ/NQ mix"): use what is on hand, and prefer the mix that
/// EMPTIES INVENTORY SLOTS fastest — HQ and NQ of one item occupy separate slots, so finishing off
/// a small stack frees one. It is recomputed from live counts before every craft, so across a run
/// the small stack drains first and the rest comes from the big one.
///
/// Not yet considered: the item's quality goal (arrives with the solver — it will constrain which
/// mixes are acceptable before this slot-freeing preference picks among them).
/// </summary>
public static class IngredientAllocator
{
    /// <returns>The mix to assign, or null when there is not enough on hand in total.</returns>
    public static (int Nq, int Hq)? Allocate(int needed, int nqAvailable, int hqAvailable)
    {
        if (needed <= 0)
            return (0, 0);
        if (nqAvailable + hqAvailable < needed)
            return null;

        // A stack can be emptied by this craft when the craft needs at least all of it.
        var canEmptyNq = nqAvailable > 0 && nqAvailable <= needed;
        var canEmptyHq = hqAvailable > 0 && hqAvailable <= needed;

        // Empty the smaller emptiable stack first (ties keep the HQ), the rest from the other.
        if (canEmptyHq && (!canEmptyNq || hqAvailable < nqAvailable))
            return (needed - hqAvailable, hqAvailable);
        if (canEmptyNq)
            return (nqAvailable, needed - nqAvailable);

        // Neither stack can be emptied by this craft: spend NQ, which vendors can replace.
        return nqAvailable >= needed ? (needed, 0) : (0, needed);
    }
}
