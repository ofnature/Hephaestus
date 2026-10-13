using System;

namespace Hephaestus.Features.Crafting;

/// <summary>
/// How many times a recipe can be made from what is on hand. The scarcest input decides — and
/// crystals are inputs like any other: three materials for fifty crafts and shards for two is two.
/// </summary>
public static class CraftableCount
{
    /// <param name="inputs">Per material AND crystal: how many one craft needs, how many are in the bags (NQ + HQ).</param>
    public static int From(ReadOnlySpan<(int Need, int Have)> inputs)
    {
        var count = int.MaxValue;
        foreach (var (need, have) in inputs)
        {
            if (need > 0)
                count = Math.Min(count, Math.Max(0, have) / need);
        }

        // A recipe that needs nothing is not one we have read properly: promise nothing.
        return count == int.MaxValue ? 0 : count;
    }
}
