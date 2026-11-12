using System;

namespace Hephaestus.Features.Crafting.Simulation;

/// <summary>
/// The chance of a high-quality item by how much of the quality bar is filled - the game's own table,
/// the figure the Synthesis window and the "trial synthesis" message show (82% of the bar is "HQ 76%":
/// the user's Courtly Lover's Partisan, 17,491 of 21,200, 2026-11-12). It is far from a straight line: half
/// the bar is 15%, three quarters 47%, nine tenths 86%, and only the full bar is certain.
/// </summary>
public static class HqChance
{
    /// <summary>Indexed by whole percent of the quality bar, 0 to 100.</summary>
    private static readonly byte[] ByPercent =
    {
        1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8,
        9, 9, 9, 10, 10, 10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14, 14, 14, 15, 15, 15, 16, 16, 17, 17,
        17, 18, 18, 18, 19, 19, 20, 20, 21, 22, 23, 24, 26, 28, 31, 34, 38, 42, 47, 52, 58, 64, 68, 71,
        74, 76, 78, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 94, 96, 98, 100,
    };

    /// <summary>Chance in percent, 1 to 100, of a high-quality item finished at <paramref name="quality"/>.</summary>
    public static int Percent(int quality, int maxQuality)
    {
        if (maxQuality <= 0)
            return 100;

        var filled = (int)((long)Math.Max(0, quality) * 100 / maxQuality);
        return ByPercent[Math.Min(100, filled)];
    }
}
