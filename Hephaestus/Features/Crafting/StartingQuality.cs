using System;
using System.Collections.Generic;

namespace Hephaestus.Features.Crafting;

/// <summary>One HQ-able ingredient of a craft: its item level, how many the recipe takes, how many of those will be HQ.</summary>
public readonly record struct QualityMaterial(long ItemLevel, int Amount, int Hq);

/// <summary>
/// The quality a craft starts with from HQ materials — the game's own rule as Artisan and GBR's
/// Vulcan carry it: each HQ-able ingredient weighs by its item level, and the HQ share of that
/// weight, times the recipe's MaterialQualityFactor, is the share of max quality granted.
/// Ingredients that cannot be HQ (crystals, NQ-only items) count for nothing on either side, so the
/// caller leaves them out. Integer arithmetic in the game's order, so this matches what the
/// Synthesis window shows at step 1 — which is what the live Raphael request key carries.
/// </summary>
public static class StartingQuality
{
    public static int Compute(IReadOnlyList<QualityMaterial> materials, int maxQuality, int materialQualityFactor)
    {
        if (materialQualityFactor <= 0 || maxQuality <= 0)
            return 0;

        long sumLevel = 0;
        long sumLevelHq = 0;
        foreach (var material in materials)
        {
            if (material.ItemLevel <= 0 || material.Amount <= 0)
                continue;

            sumLevel += material.ItemLevel * material.Amount;
            sumLevelHq += material.ItemLevel * Math.Clamp(material.Hq, 0, material.Amount);
        }

        return sumLevel == 0 ? 0 : (int)(sumLevelHq * maxQuality * materialQualityFactor / 100 / sumLevel);
    }
}
