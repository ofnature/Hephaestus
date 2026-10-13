using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

public class ListRequestTests
{
    // The game knows recipes 100-199; items 5000-5099 are craftable by recipe (itemId - 4900).
    private static ListRequest.Result Parse(string? json) => ListRequest.Parse(
        json,
        recipeExists: id => id is >= 100 and < 200,
        recipeForItem: item => item is >= 5000 and < 5100 ? item - 4900 : null);

    [Fact]
    public void AListOfRecipes_BecomesACraftList_InTheOrderGiven()
    {
        var result = Parse("""{ "items": [ { "recipeId": 150, "quantity": 12 }, { "recipeId": 101, "quantity": 2 } ] }""");

        Assert.True(result.Ok);
        Assert.Equal(new uint[] { 150, 101 }, result.List!.Items.Select(i => i.RecipeId));
        Assert.Equal(new[] { 12, 2 }, result.List.Items.Select(i => i.Quantity));
        Assert.All(result.List.Items, i => Assert.Equal(0, i.Crafted));
    }

    /// <summary>For callers that know WHAT they want but not how it is made.</summary>
    [Fact]
    public void AnEntryMayNameAnItem_InsteadOfARecipe()
    {
        var result = Parse("""{ "items": [ { "itemId": 5057, "quantity": 3 } ] }""");

        Assert.Equal(157u, result.List!.Items.Single().RecipeId);
    }

    [Fact]
    public void Quantity_DefaultsToOne()
    {
        Assert.Equal(1, Parse("""{ "items": [ { "recipeId": 150 } ] }""").List!.Items.Single().Quantity);
    }

    /// <summary>A caller that lists sub-crafts first means it — unlike the player's own queue, order is kept unless it says otherwise.</summary>
    [Fact]
    public void KeepOrder_DefaultsToTrue_AndCanBeTurnedOff()
    {
        Assert.True(Parse("""{ "items": [ { "recipeId": 150 } ] }""").List!.KeepOrder);
        Assert.False(Parse("""{ "keepOrder": false, "items": [ { "recipeId": 150 } ] }""").List!.KeepOrder);
    }

    [Fact]
    public void TheSameRecipeTwice_IsOneLine()
    {
        var result = Parse("""{ "items": [ { "recipeId": 150, "quantity": 2 }, { "recipeId": 150, "quantity": 3 } ] }""");

        Assert.Equal(5, result.List!.Items.Single().Quantity);
    }

    [Fact]
    public void PropertyNames_AreCaseInsensitive_AndUnknownFieldsAreIgnored()
    {
        var result = Parse("""{ "KeepOrder": false, "requestedBy": "Odysseus", "Items": [ { "RecipeID": 150, "Quantity": 2, "note": "for the delivery" } ] }""");

        Assert.True(result.Ok);
        Assert.Equal(2, result.List!.Items.Single().Quantity);
    }

    /// <summary>
    /// ALL OR NOTHING. Crafting "the parts I understood" would leave the caller guessing which items
    /// exist afterwards; a flat refusal with a reason it can log and fix.
    /// </summary>
    [Theory]
    [InlineData("""{ "items": [ { "recipeId": 150 }, { "recipeId": 999 } ] }""", "no recipe 999")]
    [InlineData("""{ "items": [ { "recipeId": 150 }, { "itemId": 42 } ] }""", "cannot be crafted")]
    [InlineData("""{ "items": [ { "recipeId": 150, "quantity": 0 } ] }""", "at least 1")]
    [InlineData("""{ "items": [ { "recipeId": 150, "quantity": -4 } ] }""", "at least 1")]
    [InlineData("""{ "items": [ { "quantity": 3 } ] }""", "neither")]
    [InlineData("""{ "items": [ null ] }""", "null")]
    public void OneBadEntry_RejectsTheWholeRequest_WithTheReason(string json, string expectedError)
    {
        var result = Parse(json);

        Assert.False(result.Ok);
        Assert.Null(result.List);
        Assert.Contains(expectedError, result.Error);
    }

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("not json at all", "not valid JSON")]
    [InlineData("""{ "items": [ { "recipeId": "many" } ] }""", "not valid JSON")]
    [InlineData("{}", "no items")]
    [InlineData("""{ "items": [] }""", "no items")]
    [InlineData("null", "no items")]
    public void AMalformedRequest_IsRefused_NeverThrown(string? json, string expectedError)
    {
        var result = Parse(json);

        Assert.False(result.Ok);
        Assert.Contains(expectedError, result.Error);
    }

    [Fact]
    public void ARecipeIdWins_WhenBothAreGiven()
    {
        Assert.Equal(150u, Parse("""{ "items": [ { "recipeId": 150, "itemId": 5001 } ] }""").List!.Items.Single().RecipeId);
    }
}
