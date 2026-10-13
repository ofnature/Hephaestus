using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Materials;

/// <summary>Gathering (Odysseus) as the LAST source: "you can always gather it if you can't buy it" (user, 2026-09-21).</summary>
public class MaterialPlannerGatherTests
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
    private readonly Dictionary<uint, uint> _prices = new();
    private readonly HashSet<uint> _gatherable = new() { Ore };

    private MaterialPlanner Planner() => new(
        id => AllRecipes.Where(r => r.RecipeId == id).Select(r => (RecipeInfo?)r).FirstOrDefault(),
        item => AllRecipes.Where(r => r.ResultItemId == item).Select(r => new RecipeOption(r.RecipeId, r.JobId)).ToList(),
        _ => true,
        item => _held.GetValueOrDefault(item),
        item => $"item {item}");

    private VendorSupply Vendors(long gil) => new(item => _prices.GetValueOrDefault(item), gil);

    private GatherSupply Gathering() => new(_gatherable.Contains);

    private static CraftListItem Entry(uint recipeId, int quantity) => new() { RecipeId = recipeId, Quantity = quantity };

    [Fact]
    public void WhatNoVendorSells_IsGathered_ForExactlyTheShortfall()
    {
        _held[Ore] = 2;
        var plate = Entry(23, 1); // 2 ingots = 6 ore

        var plan = Planner().Plan(new[] { plate }, Armorer, vendors: Vendors(1000), gathering: Gathering());

        var ore = Assert.Single(plan.Gathers);
        Assert.Equal((Ore, 4), (ore.ItemId, ore.Quantity));
        Assert.Equal(4, ore.ByEntry[plate]);
        Assert.Empty(plan.Purchases);
        Assert.Empty(plan.Shortfalls);
        Assert.Equal(2, Assert.Single(plan.SubCrafts).Crafts); // the ingots are planned on the strength of it
        Assert.Equal(1, plan.Achievable(plate));
    }

    /// <summary>The source order: a vendor that sells it is used first, for as much as the gil allows; gathering covers the rest.</summary>
    [Fact]
    public void AVendorComesFirst_AndGatheringTakesWhatTheGilDoesNotCover()
    {
        _prices[Ore] = 10;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer, vendors: Vendors(gil: 40), gathering: Gathering());

        Assert.Equal(4, Assert.Single(plan.Purchases).Quantity);
        Assert.Equal(2, Assert.Single(plan.Gathers).Quantity);
        Assert.Empty(plan.Shortfalls);
        Assert.False(plan.ShortOfGil); // gathering covered it: nothing is short for want of gil
    }

    [Fact]
    public void WithEnoughGil_NothingIsGathered()
    {
        _prices[Ore] = 10;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer, vendors: Vendors(gil: 1000), gathering: Gathering());

        Assert.Equal(6, Assert.Single(plan.Purchases).Quantity);
        Assert.Empty(plan.Gathers);
    }

    [Fact]
    public void NotSold_AndNotGatherable_StaysAShortfall()
    {
        var plan = Planner().Plan(new[] { Entry(25, 1) }, Armorer, vendors: Vendors(1000), gathering: Gathering());

        Assert.Equal(RareDrop, Assert.Single(plan.Shortfalls).ItemId);
    }

    /// <summary>The safety rule holds for gathering too: no ore is gathered for a trophy whose rare drop is missing.</summary>
    [Fact]
    public void NothingIsGatheredTowardsAnEntry_ThatCannotBeFinishedAnyway()
    {
        var plan = Planner().Plan(new[] { Entry(25, 3) }, Armorer, vendors: Vendors(1000), gathering: Gathering());

        Assert.Empty(plan.Gathers);
        Assert.Empty(plan.SubCrafts);
    }

    [Fact]
    public void WithoutGathering_WhatVendorsDoNotCover_IsAShortfall()
    {
        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer, vendors: Vendors(1000));

        Assert.Empty(plan.Gathers);
        Assert.Equal((Ore, 6), (plan.Shortfalls[0].ItemId, plan.Shortfalls[0].Missing));
    }
}
