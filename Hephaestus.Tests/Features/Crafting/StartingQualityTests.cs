using Hephaestus.Features.Crafting;

namespace Hephaestus.Tests.Features.Crafting;

/// <summary>The formula as Artisan and GBR's Vulcan apply it; the numbers below are worked by hand from it.</summary>
public class StartingQualityTests
{
    [Fact]
    public void HqShareOfItemLevelWeight_TimesTheFactor_IsTheStartingQuality()
    {
        // (50*1) * 1000 * 50 / 100 / (50*2 + 30*1) = 25000 / 130 = 192 (integer)
        var materials = new[] { new QualityMaterial(ItemLevel: 50, Amount: 2, Hq: 1), new QualityMaterial(30, 1, 0) };

        Assert.Equal(192, StartingQuality.Compute(materials, maxQuality: 1000, materialQualityFactor: 50));
    }

    [Fact]
    public void AllHq_GivesTheFactorShareOfMaxQuality()
    {
        var materials = new[] { new QualityMaterial(50, 2, 2), new QualityMaterial(30, 1, 1) };

        Assert.Equal(500, StartingQuality.Compute(materials, 1000, 50));
    }

    [Fact]
    public void NoHq_OrNoFactor_OrNothingHqAble_IsZero()
    {
        Assert.Equal(0, StartingQuality.Compute(new[] { new QualityMaterial(50, 2, 0) }, 1000, 50));
        Assert.Equal(0, StartingQuality.Compute(new[] { new QualityMaterial(50, 2, 2) }, 1000, 0));
        Assert.Equal(0, StartingQuality.Compute(System.Array.Empty<QualityMaterial>(), 1000, 50));
    }

    [Fact]
    public void MoreHqThanTheRecipeTakes_CountsOnlyWhatItTakes()
    {
        Assert.Equal(500, StartingQuality.Compute(new[] { new QualityMaterial(50, 2, 9) }, 1000, 50));
    }
}
