using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Materials;

/// <summary>Vendors as the LAST source: after the bags, after sub-crafting, within the gil held.</summary>
public class MaterialPlannerVendorTests
{
    private const uint Blacksmith = 9;
    private const uint Armorer = 10;

    private const uint Ore = 1;
    private const uint Shard = 2;
    private const uint RareDrop = 4;
    private const uint Ingot = 100;

    private static readonly RecipeInfo[] AllRecipes =
    {
        new(20, Blacksmith, "Bronze Ingot", Ingot, new RecipeInput[] { new(Ore, 3), new(Shard, 1) }),
        new(23, Armorer, "Bronze Plate", 103, new RecipeInput[] { new(Ingot, 2) }),
        new(25, Armorer, "Trophy", 105, new RecipeInput[] { new(Ingot, 1), new(RareDrop, 1) }),
    };

    private readonly Dictionary<uint, int> _held = new() { [Shard] = 99 };
    private readonly Dictionary<uint, uint> _prices = new() { [Ore] = 10 };
    private readonly HashSet<uint> _jobs = new() { Blacksmith, Armorer };

    private MaterialPlanner Planner() => new(
        id => AllRecipes.Where(r => r.RecipeId == id).Select(r => (RecipeInfo?)r).FirstOrDefault(),
        item => AllRecipes.Where(r => r.ResultItemId == item).Select(r => new RecipeOption(r.RecipeId, r.JobId)).ToList(),
        recipe => _jobs.Contains(recipe.JobId),
        item => _held.GetValueOrDefault(item),
        item => $"item {item}");

    private VendorSupply Vendors(long gil) => new(item => _prices.GetValueOrDefault(item), gil);

    private static CraftListItem Entry(uint recipeId, int quantity) => new() { RecipeId = recipeId, Quantity = quantity };

    [Fact]
    public void WhatCannotBeCrafted_IsBought_ForExactlyTheShortfall()
    {
        _held[Ore] = 2; // one plate = two ingots = six ore
        var plate = Entry(23, 1);

        var plan = Planner().Plan(new[] { plate }, Armorer, vendors: Vendors(gil: 1000));

        var ore = Assert.Single(plan.Purchases);
        Assert.Equal((Ore, 4, 10u, 40L), (ore.ItemId, ore.Quantity, ore.UnitPrice, ore.Cost));
        Assert.Equal(40, plan.PurchaseCost);
        Assert.Equal(4, ore.ByEntry[plate]);
        Assert.Equal(2, Assert.Single(plan.SubCrafts).Crafts); // and the ingots are planned on the strength of it
        Assert.Equal(1, plan.Achievable(plate));
        Assert.Empty(plan.Shortfalls);
        Assert.False(plan.ShortOfGil);
    }

    /// <summary>The source order: sub-craft before vendor. Ingots are for sale here too, and are still smelted.</summary>
    [Fact]
    public void ACraftableIntermediate_IsCrafted_EvenWhenAVendorSellsIt()
    {
        _prices[Ingot] = 500;
        _held[Ore] = 6;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer, vendors: Vendors(gil: 100_000));

        Assert.Empty(plan.Purchases);
        Assert.Equal(2, Assert.Single(plan.SubCrafts).Crafts);
    }

    [Fact]
    public void AnIntermediateThisCharacterCannotCraft_IsBoughtInstead()
    {
        _jobs.Remove(Blacksmith);
        _prices[Ingot] = 500;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer, vendors: Vendors(gil: 100_000));

        Assert.Empty(plan.SubCrafts);
        Assert.Equal((Ingot, 2), (plan.Purchases[0].ItemId, plan.Purchases[0].Quantity));
    }

    [Fact]
    public void WhatNoVendorSells_StaysAShortfall()
    {
        var plan = Planner().Plan(new[] { Entry(25, 1) }, Armorer, vendors: Vendors(gil: 100_000));

        Assert.Equal(RareDrop, Assert.Single(plan.Shortfalls).ItemId);
        Assert.False(plan.ShortOfGil);
    }

    /// <summary>The safety rule covers gil too: no ore is bought for a trophy whose rare drop is missing.</summary>
    [Fact]
    public void NothingIsBoughtTowardsAnEntry_ThatCannotBeFinishedAnyway()
    {
        var plan = Planner().Plan(new[] { Entry(25, 3) }, Armorer, vendors: Vendors(gil: 100_000));

        Assert.Empty(plan.Purchases);
        Assert.Empty(plan.SubCrafts);
    }

    [Fact]
    public void AnEntryPartlyCovered_BuysOnlyForThePartThatCanBeFinished()
    {
        _held[RareDrop] = 2;

        var plan = Planner().Plan(new[] { Entry(25, 5) }, Armorer, vendors: Vendors(gil: 100_000));

        Assert.Equal(6, Assert.Single(plan.Purchases).Quantity); // two trophies -> two ingots -> six ore
        Assert.Equal(3, Assert.Single(plan.Shortfalls).Missing); // the rare drop for the other three, and nothing else
    }

    /// <summary>A free-trial character holds at most 300k: what it cannot pay for, it cannot count on.</summary>
    [Fact]
    public void Gil_IsALimit_AndIsSpentInListOrder()
    {
        var first = Entry(23, 1);  // 6 ore = 60 gil
        var second = Entry(23, 1); // 60 more

        var plan = Planner().Plan(new[] { first, second }, Armorer, vendors: Vendors(gil: 100));

        Assert.Equal(1, plan.Achievable(first));
        Assert.Equal(0, plan.Achievable(second));
        var ore = Assert.Single(plan.Purchases);
        Assert.Equal(6, ore.Quantity);
        Assert.False(ore.ByEntry.ContainsKey(second));
        Assert.True(plan.ShortOfGil);
        Assert.Equal(Ore, Assert.Single(plan.Shortfalls).ItemId);
    }

    [Fact]
    public void WithoutVendors_NothingIsEverPlannedToBeBought()
    {
        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer);

        Assert.Empty(plan.Purchases);
        Assert.Equal((Ore, 6), (plan.Shortfalls[0].ItemId, plan.Shortfalls[0].Missing));
    }
}
