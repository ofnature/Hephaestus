using Hephaestus.Features.Crafting;

namespace Hephaestus.Tests.Features.Crafting;

public class CraftableCountTests
{
    [Fact]
    public void TheScarcestInput_Decides()
    {
        // 3 per craft from 33 = 11; 2 per craft from 9 = 4; 1 per craft from 50 = 50.
        Assert.Equal(4, CraftableCount.From([(3, 33), (2, 9), (1, 50)]));
    }

    /// <summary>The case that prompted this: plenty of materials, not enough shards.</summary>
    [Fact]
    public void Crystals_CountLikeAnyOtherInput()
    {
        Assert.Equal(2, CraftableCount.From([(3, 150), (3, 150), (3, 7)]));
    }

    [Fact]
    public void OneMissingInput_MeansNone()
    {
        Assert.Equal(0, CraftableCount.From([(3, 0), (3, 0), (2, 0), (3, 33), (3, 120), (3, 75)]));
    }

    [Fact]
    public void ALeftoverThatDoesNotMakeAWholeCraft_DoesNotCount()
    {
        Assert.Equal(3, CraftableCount.From([(3, 11)]));
    }

    /// <summary>Empty recipe slots (need 0) are skipped; a bad negative count never becomes a negative answer.</summary>
    [Fact]
    public void EmptySlotsAreIgnored_AndNothingReadIsNothingPromised()
    {
        Assert.Equal(5, CraftableCount.From([(0, 0), (2, 10), (0, 99)]));
        Assert.Equal(0, CraftableCount.From([(2, -4)]));
        Assert.Equal(0, CraftableCount.From([]));
        Assert.Equal(0, CraftableCount.From([(0, 10)]));
    }
}
