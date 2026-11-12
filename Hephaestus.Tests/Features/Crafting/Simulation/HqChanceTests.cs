using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Tests.Features.Crafting.Simulation;

public class HqChanceTests
{
    [Fact]
    public void TheChance_IsWhatTheGameShowed_OnTheUsersOwnCraft()
    {
        // Courtly Lover's Partisan, trial synthesis, 2026-11-12: "Quality 17491/21200 ... HQ 76%".
        Assert.Equal(76, HqChance.Percent(17491, 21200));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 15)]
    [InlineData(75, 47)]
    [InlineData(90, 86)]
    [InlineData(99, 98)]
    [InlineData(100, 100)]
    public void TheTable_AtItsLandmarks(int percentOfTheBar, int chance)
    {
        Assert.Equal(chance, HqChance.Percent(percentOfTheBar * 100, 10000));
    }

    [Fact]
    public void MoreQuality_IsNeverALowerChance_AndOnlyTheFullBarIsCertain()
    {
        var last = 0;
        for (var quality = 0; quality <= 21200; quality += 53)
        {
            var chance = HqChance.Percent(quality, 21200);
            Assert.True(chance >= last, $"{quality}: {chance} after {last}");
            Assert.InRange(chance, 1, quality >= 21200 ? 100 : 98);
            last = chance;
        }

        Assert.Equal(100, HqChance.Percent(21200, 21200));
        Assert.Equal(100, HqChance.Percent(99999, 21200)); // over the bar is the bar
    }

    [Fact]
    public void AnItemWithNoQualityBar_IsNotAQuestion()
    {
        Assert.Equal(100, HqChance.Percent(0, 0));
    }
}
