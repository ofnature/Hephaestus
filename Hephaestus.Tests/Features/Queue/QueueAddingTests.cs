using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

public class QueueAddingTests
{
    private const uint Carpenter = 8;
    private const uint Blacksmith = 9;
    private const uint Armorer = 10;

    // ---- CraftList.Add ---------------------------------------------------------------------------------

    [Fact]
    public void AddingANewRecipe_AppendsIt()
    {
        var list = new CraftList();

        var item = list.Add(100, 3);

        Assert.Same(item, list.Items.Single());
        Assert.Equal(3, item!.Quantity);
        Assert.Equal(0, item.Crafted);
    }

    /// <summary>Clicking "add" three times should read as one line of three, not three lines.</summary>
    [Fact]
    public void AddingTheSameRecipeAgain_TopsUpTheOpenEntry()
    {
        var list = new CraftList();
        list.Add(100, 1);
        list.Add(200, 1);

        var again = list.Add(100, 4);

        Assert.Equal(2, list.Items.Count);
        Assert.Same(list.Items[0], again);
        Assert.Equal(5, list.Items[0].Quantity);
    }

    [Fact]
    public void AnEntryBeingWorkedOn_IsStillOpen()
    {
        var list = new CraftList();
        list.Add(100, 5)!.Crafted = 2;

        list.Add(100, 3);

        Assert.Single(list.Items);
        Assert.Equal(8, list.Items[0].Quantity);
        Assert.Equal(2, list.Items[0].Crafted);
    }

    /// <summary>A finished entry is history — it is not reopened.</summary>
    [Fact]
    public void AFinishedEntry_IsNotReopened()
    {
        var list = new CraftList();
        list.Add(100, 2)!.Crafted = 2;

        list.Add(100, 1);

        Assert.Equal(2, list.Items.Count);
        Assert.Equal(2, list.Items[0].Quantity);
        Assert.Equal(1, list.Items[1].Quantity);
    }

    /// <summary>A flagged entry is waiting for review; quietly growing it would hide that.</summary>
    [Fact]
    public void AFlaggedEntry_IsNotToppedUp()
    {
        var list = new CraftList();
        list.Add(100, 2)!.SkipReason = "out of materials";

        list.Add(100, 1);

        Assert.Equal(2, list.Items.Count);
        Assert.Equal("out of materials", list.Items[0].SkipReason);
        Assert.Null(list.Items[1].SkipReason);
    }

    [Fact]
    public void TheMostRecentEntryDecides_NotAnOlderOne()
    {
        var list = new CraftList();
        list.Add(100, 1);                  // open, but older
        list.Items.Add(new CraftListItem { RecipeId = 100, Quantity = 1, Crafted = 1 }); // newest, finished

        list.Add(100, 2);

        Assert.Equal(3, list.Items.Count);
        Assert.Equal(1, list.Items[0].Quantity);
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(100u, 0)]
    [InlineData(100u, -5)]
    public void NothingToAdd_AddsNothing(uint recipeId, int quantity)
    {
        var list = new CraftList();

        Assert.Null(list.Add(recipeId, quantity));
        Assert.Empty(list.Items);
    }

    // ---- RecipeChoice ----------------------------------------------------------------------------------

    private static readonly RecipeOption[] ThreeJobs =
    {
        new(500, Carpenter), new(501, Blacksmith), new(502, Armorer),
    };

    [Fact]
    public void NoRecipes_NoChoice()
    {
        Assert.Null(RecipeChoice.Pick(Array.Empty<RecipeOption>(), Carpenter, Carpenter));
    }

    /// <summary>In the crafting log the job tab being browsed IS the recipe the player is looking at.</summary>
    [Fact]
    public void TheJobTheRequestCameFrom_WinsOverTheJobYouAreOn()
    {
        Assert.Equal(502u, RecipeChoice.Pick(ThreeJobs, contextJob: Armorer, currentJob: Blacksmith)!.Value.RecipeId);
    }

    /// <summary>With no context, the job you are already on costs no gearset change.</summary>
    [Fact]
    public void OtherwiseTheJobYouAreOn()
    {
        Assert.Equal(501u, RecipeChoice.Pick(ThreeJobs, contextJob: 0, currentJob: Blacksmith)!.Value.RecipeId);
    }

    [Fact]
    public void AContextJobThatCannotMakeIt_FallsThroughToTheCurrentJob()
    {
        Assert.Equal(501u, RecipeChoice.Pick(ThreeJobs, contextJob: 15, currentJob: Blacksmith)!.Value.RecipeId);
    }

    [Fact]
    public void WhenNeitherJobMakesIt_TheFirstRecipe()
    {
        Assert.Equal(500u, RecipeChoice.Pick(ThreeJobs, contextJob: 0, currentJob: 1)!.Value.RecipeId);
        Assert.Equal(500u, RecipeChoice.Pick(ThreeJobs, contextJob: 0, currentJob: 0)!.Value.RecipeId);
    }
}
