using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Materials;

public class SupplyListTests
{
    private const uint Blacksmith = 9;
    private const uint Carpenter = 8;

    private const uint Ore = 1;
    private const uint Shard = 2;
    private const uint Log = 3;
    private const uint RareDrop = 4;
    private const uint Ingot = 100;
    private const uint Lumber = 101;

    private static readonly RecipeInfo[] AllRecipes =
    {
        new(20, Blacksmith, "Bronze Ingot", Ingot, new RecipeInput[] { new(Ore, 3), new(Shard, 1) }),
        new(21, Carpenter, "Maple Lumber", Lumber, new RecipeInput[] { new(Log, 3) }),
        new(24, Carpenter, "Maple Shield", 104, new RecipeInput[] { new(Ingot, 2), new(Lumber, 1) }),
        new(25, Carpenter, "Trophy", 105, new RecipeInput[] { new(Lumber, 1), new(RareDrop, 1) }),
    };

    private readonly Dictionary<uint, int> _held = new() { [Shard] = 99 };
    private readonly DateTime _now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private MaterialPlanner Planner() => new(
        id => AllRecipes.Where(r => r.RecipeId == id).Select(r => (RecipeInfo?)r).FirstOrDefault(),
        item => AllRecipes.Where(r => r.ResultItemId == item).Select(r => new RecipeOption(r.RecipeId, r.JobId)).ToList(),
        _ => true,
        item => _held.GetValueOrDefault(item),
        item => item switch { Ore => "Copper Ore", Log => "Maple Log", RareDrop => "Rare Drop", _ => $"item {item}" });

    /// <summary>Ore is sold (2 gil); logs are not, but can be gathered.</summary>
    private (SupplyList Buy, SupplyList Gather) Make(params CraftListItem[] entries)
        => SupplyList.Make(
            Planner(), entries, Carpenter,
            new VendorSupply(item => item == Ore ? 2u : 0u, 100_000),
            new GatherSupply(item => item == Log),
            id => AllRecipes.First(r => r.RecipeId == id).Name,
            _now);

    private static CraftListItem Entry(uint recipeId, int quantity, int crafted = 0) => new() { RecipeId = recipeId, Quantity = quantity, Crafted = crafted };

    [Fact]
    public void OnePlan_SplitsIntoABuyListAndAGatherList_NeverClaimingTheSameShortfallTwice()
    {
        _held[Ore] = 6;

        var (buy, gather) = Make(Entry(24, 3)); // 6 ingots = 18 ore; 3 lumber = 9 logs

        var ore = Assert.Single(buy.Lines);
        Assert.Equal((Ore, "Copper Ore", 18, 6, 12), (ore.ItemId, ore.Name, ore.Needed, ore.InBags, ore.Missing));
        Assert.Equal(2u, ore.UnitPrice);
        Assert.Equal(new[] { "Maple Shield" }, ore.For);

        var logs = Assert.Single(gather.Lines);
        Assert.Equal((Log, 9, 0, 9), (logs.ItemId, logs.Needed, logs.InBags, logs.Missing));

        Assert.Equal(24, buy.CostOfMissing);
        Assert.Equal(_now, buy.MadeAt);
    }

    /// <summary>A flag is a request for review; buying its materials IS the review, so flagged entries are listed.</summary>
    [Fact]
    public void FlaggedEntries_AreIncluded_FinishedOnesAreNot()
    {
        var flagged = Entry(24, 1);
        flagged.SkipReason = "short of 6 × Copper Ore";

        var (buy, _) = Make(flagged, Entry(24, 2, crafted: 2));

        Assert.Equal(6, Assert.Single(buy.Lines).Needed);
    }

    [Fact]
    public void InBags_FollowsTheInventory_AndWhatIsOutstandingShrinks()
    {
        var (buy, _) = Make(Entry(24, 1));
        Assert.Equal(new[] { (Ore, 6, 6) }, buy.Outstanding().Select(f => (f.ItemId, f.Target, f.Quantity)));

        _held[Ore] = 4;
        buy.RefreshBags(item => _held.GetValueOrDefault(item));

        Assert.Equal(new[] { (Ore, 6, 2) }, buy.Outstanding().Select(f => (f.ItemId, f.Target, f.Quantity)));
        Assert.Equal(4, buy.CostOfMissing);
        Assert.False(buy.AllDone);

        _held[Ore] = 6;
        buy.RefreshBags(item => _held.GetValueOrDefault(item));
        Assert.True(buy.AllDone);
        Assert.Empty(buy.Outstanding());
    }

    /// <summary>
    /// "Follow the bags" (user, 2026-09-21): a paid account buys the ingots on the market board instead of
    /// smelting them. The list shows them as intermediates; once they are held, a Refresh drops their ore.
    /// </summary>
    [Fact]
    public void BoughtSubCombines_DropTheirMaterials_OnTheNextRefresh()
    {
        var (before, _) = Make(Entry(24, 1));
        Assert.Equal(6, Assert.Single(before.Lines).Needed);
        var ingots = Assert.Single(before.Intermediates, i => i.ItemId == Ingot);
        Assert.Equal(2, ingots.Quantity);

        _held[Ingot] = 2;
        var (after, _) = Make(Entry(24, 1));

        Assert.Empty(after.Lines);
        Assert.DoesNotContain(after.Intermediates, i => i.ItemId == Ingot);
    }

    [Fact]
    public void WhatCannotBeBoughtOrGathered_IsListedAsUnobtainable_OnBothLists()
    {
        var (buy, gather) = Make(Entry(25, 2));

        var rare = Assert.Single(buy.Unobtainable);
        Assert.Equal((RareDrop, "Rare Drop", 2), (rare.ItemId, rare.Name, rare.Missing));
        Assert.Same(buy.Unobtainable, gather.Unobtainable);
    }
}
