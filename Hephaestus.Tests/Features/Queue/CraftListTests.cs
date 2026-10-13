using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

public class CraftListTests
{
    private static CraftList ThreeItems() => new()
    {
        Items =
        {
            new CraftListItem { RecipeId = 1, Quantity = 3, Crafted = 3 },
            new CraftListItem { RecipeId = 2, Quantity = 5, Crafted = 2 },
            new CraftListItem { RecipeId = 3, Quantity = 1 },
        },
    };

    [Fact]
    public void Totals_SumAcrossItems()
    {
        var list = ThreeItems();

        Assert.Equal(9, list.TotalQuantity);
        Assert.Equal(5, list.TotalCrafted);
    }

    /// <summary>Lowering a quantity below what is already made must not push progress past 100%.</summary>
    [Fact]
    public void TotalCrafted_NeverExceedsQuantity()
    {
        var list = new CraftList { Items = { new CraftListItem { RecipeId = 1, Quantity = 2, Crafted = 6 } } };

        Assert.Equal(2, list.TotalCrafted);
    }

    /// <summary>This is the whole resume mechanism: progress is in the list, so resuming is a lookup.</summary>
    [Fact]
    public void NextPending_SkipsFinishedItems()
    {
        Assert.Equal(2u, ThreeItems().NextPending()!.RecipeId);
    }

    [Fact]
    public void NextPending_AllDoneOrEmpty_IsNull()
    {
        Assert.Null(new CraftList().NextPending());
        Assert.Null(new CraftList { Items = { new CraftListItem { RecipeId = 1, Quantity = 1, Crafted = 1 } } }.NextPending());
    }

    [Fact]
    public void Move_SwapsNeighbours()
    {
        var list = ThreeItems();

        list.Move(0, +1);

        Assert.Equal(new uint[] { 2, 1, 3 }, list.Items.Select(i => i.RecipeId));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(2, +1)]
    [InlineData(7, -1)]
    [InlineData(-1, +1)]
    public void Move_OutOfRange_IsIgnored(int index, int direction)
    {
        var list = ThreeItems();

        list.Move(index, direction);

        Assert.Equal(new uint[] { 1, 2, 3 }, list.Items.Select(i => i.RecipeId));
    }
}
