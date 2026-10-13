using Hephaestus.Features.Crafting;

namespace Hephaestus.Tests.Features.Crafting;

public class IngredientAllocatorTests
{
    [Fact]
    public void NotEnoughInTotal_IsNull()
    {
        Assert.Null(IngredientAllocator.Allocate(needed: 3, nqAvailable: 1, hqAvailable: 1));
    }

    [Fact]
    public void NothingNeeded_AssignsNothing()
    {
        Assert.Equal((0, 0), IngredientAllocator.Allocate(0, 50, 50));
    }

    [Theory]
    [InlineData(3, 40, 0, 3, 0)]
    [InlineData(3, 0, 40, 0, 3)]
    public void OnlyOneKindOnHand_UsesIt(int needed, int nq, int hq, int expectNq, int expectHq)
    {
        Assert.Equal((expectNq, expectHq), IngredientAllocator.Allocate(needed, nq, hq));
    }

    /// <summary>The "maximize space" rule: a small HQ stack is finished off so its slot frees up.</summary>
    [Fact]
    public void SmallHqStack_IsEmptiedFirst()
    {
        Assert.Equal((1, 2), IngredientAllocator.Allocate(needed: 3, nqAvailable: 40, hqAvailable: 2));
    }

    [Fact]
    public void SmallNqStack_IsEmptiedFirst()
    {
        Assert.Equal((2, 1), IngredientAllocator.Allocate(needed: 3, nqAvailable: 2, hqAvailable: 40));
    }

    [Fact]
    public void BothStacksSmall_EmptiesTheSmallerOne()
    {
        Assert.Equal((2, 1), IngredientAllocator.Allocate(needed: 3, nqAvailable: 3, hqAvailable: 1));
        Assert.Equal((1, 2), IngredientAllocator.Allocate(needed: 3, nqAvailable: 1, hqAvailable: 3));
    }

    /// <summary>Equal small stacks: spend the NQ one, keep the HQ.</summary>
    [Fact]
    public void BothStacksSmallAndEqual_KeepsTheHq()
    {
        Assert.Equal((2, 1), IngredientAllocator.Allocate(needed: 3, nqAvailable: 2, hqAvailable: 2));
    }

    /// <summary>Neither slot can be freed by this craft, so spend what a vendor can replace.</summary>
    [Fact]
    public void BothStacksLarge_SpendsNq()
    {
        Assert.Equal((3, 0), IngredientAllocator.Allocate(needed: 3, nqAvailable: 40, hqAvailable: 40));
    }

    [Theory]
    [InlineData(3, 40, 2)]
    [InlineData(3, 2, 40)]
    [InlineData(5, 3, 3)]
    [InlineData(1, 1, 1)]
    [InlineData(6, 6, 6)]
    [InlineData(4, 99, 99)]
    public void Allocation_AlwaysSumsToNeeded_AndNeverExceedsWhatIsOnHand(int needed, int nq, int hq)
    {
        var mix = IngredientAllocator.Allocate(needed, nq, hq)!.Value;

        Assert.Equal(needed, mix.Nq + mix.Hq);
        Assert.InRange(mix.Nq, 0, nq);
        Assert.InRange(mix.Hq, 0, hq);
    }

    /// <summary>Recomputed from live counts each craft, a run drains the small stack then moves on.</summary>
    [Fact]
    public void AcrossARun_TheSmallStackDrainsThenTheBigOneTakesOver()
    {
        int nq = 40, hq = 2;

        var first = IngredientAllocator.Allocate(3, nq, hq)!.Value;
        nq -= first.Nq;
        hq -= first.Hq;
        var second = IngredientAllocator.Allocate(3, nq, hq)!.Value;

        Assert.Equal((1, 2), first);
        Assert.Equal(0, hq);
        Assert.Equal((3, 0), second);
    }
}
