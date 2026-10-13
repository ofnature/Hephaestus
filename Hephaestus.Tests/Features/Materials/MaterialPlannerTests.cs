using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Materials;

public class MaterialPlannerTests
{
    private const uint Carpenter = 8;
    private const uint Blacksmith = 9;
    private const uint Armorer = 10;

    // Raw materials.
    private const uint Ore = 1;
    private const uint Shard = 2;
    private const uint Log = 3;
    private const uint RareDrop = 4;

    // Craftable items.
    private const uint Ingot = 100;
    private const uint Lumber = 101;
    private const uint Nails = 102;
    private const uint Plate = 103;
    private const uint Shield = 104;
    private const uint Trophy = 105;
    private const uint Armour = 106;
    private const uint Crate = 107;
    private const uint Wheel = 108;
    private const uint MythrilIngot = 120;
    private const uint LoopA = 110;
    private const uint LoopB = 111;

    private static readonly RecipeInfo[] AllRecipes =
    {
        new(20, Blacksmith, "Bronze Ingot", Ingot, new RecipeInput[] { new(Ore, 3), new(Shard, 1) }),
        new(21, Carpenter, "Maple Lumber", Lumber, new RecipeInput[] { new(Log, 3) }),
        new(22, Blacksmith, "Bronze Nails", Nails, new RecipeInput[] { new(Ingot, 1) }, Yield: 3),
        new(23, Armorer, "Bronze Plate", Plate, new RecipeInput[] { new(Ingot, 2) }),
        new(24, Carpenter, "Maple Shield", Shield, new RecipeInput[] { new(Ingot, 2), new(Lumber, 1) }),
        new(25, Carpenter, "Trophy", Trophy, new RecipeInput[] { new(Ingot, 1), new(RareDrop, 1) }),
        new(26, Armorer, "Bronze Armour", Armour, new RecipeInput[] { new(Plate, 2) }),
        new(27, Carpenter, "Nailed Crate", Crate, new RecipeInput[] { new(Nails, 4) }),

        // One item, a recipe under two jobs.
        new(28, Carpenter, "Cart Wheel", Wheel, new RecipeInput[] { new(Log, 1) }),
        new(29, Blacksmith, "Cart Wheel", Wheel, new RecipeInput[] { new(Log, 1) }),
        new(30, Armorer, "Cart", 109, new RecipeInput[] { new(Wheel, 1) }),

        // Bad sheet data: two items that need each other.
        new(40, Carpenter, "Loop A", LoopA, new RecipeInput[] { new(LoopB, 1) }),
        new(41, Carpenter, "Loop B", LoopB, new RecipeInput[] { new(LoopA, 1) }),

        // An intermediate meant for a level-60 Blacksmith.
        new(50, Blacksmith, "Mythril Ingot", MythrilIngot, new RecipeInput[] { new(Ore, 3) }, JobLevel: 60),
        new(51, Armorer, "Mythril Plate", 121, new RecipeInput[] { new(MythrilIngot, 1) }),
    };

    private readonly Dictionary<uint, int> _held = new();
    private readonly HashSet<uint> _jobs = new() { Carpenter, Blacksmith, Armorer };
    private int _level = 50;

    private MaterialPlanner Planner() => new(
        id => AllRecipes.Where(r => r.RecipeId == id).Select(r => (RecipeInfo?)r).FirstOrDefault(),
        item => AllRecipes.Where(r => r.ResultItemId == item).Select(r => new RecipeOption(r.RecipeId, r.JobId)).ToList(),
        recipe => _jobs.Contains(recipe.JobId) && _level >= recipe.JobLevel,
        item => _held.GetValueOrDefault(item),
        item => $"item {item}");

    private static CraftListItem Entry(uint recipeId, int quantity, int crafted = 0)
        => new() { RecipeId = recipeId, Quantity = quantity, Crafted = crafted };

    [Fact]
    public void EverythingInTheBags_NothingToPlan()
    {
        _held[Ingot] = 6;
        _held[Lumber] = 3;
        var shield = Entry(24, 3);

        var plan = Planner().Plan(new[] { shield }, Carpenter);

        Assert.Empty(plan.SubCrafts);
        Assert.Empty(plan.Shortfalls);
        Assert.Equal(3, plan.Achievable(shield));
    }

    [Fact]
    public void AMissingIntermediate_BecomesASubCraft_ForExactlyTheShortfall()
    {
        _held[Ingot] = 2; // 3 shields want 6
        _held[Lumber] = 3;
        _held[Ore] = 50;
        _held[Shard] = 50;
        var shield = Entry(24, 3);

        var plan = Planner().Plan(new[] { shield }, Carpenter);

        var ingots = Assert.Single(plan.SubCrafts);
        Assert.Equal(20u, ingots.Recipe.RecipeId);
        Assert.Equal(4, ingots.Crafts);
        Assert.Same(shield, Assert.Single(ingots.For));
        Assert.Empty(plan.Shortfalls);
        Assert.Equal(3, plan.Achievable(shield));
    }

    /// <summary>Only what is still to be made counts: 2 of the 5 are done.</summary>
    [Fact]
    public void Demand_ComesFromTheRemainingCrafts()
    {
        _held[Lumber] = 9;
        _held[Ore] = 50;
        _held[Shard] = 50;

        var plan = Planner().Plan(new[] { Entry(24, 5, crafted: 2) }, Carpenter);

        Assert.Equal(6, Assert.Single(plan.SubCrafts).Crafts); // 3 shields x 2 ingots
    }

    [Fact]
    public void TheTreeIsWalkedToTheBottom_AndOrderedDeepestFirst()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;

        // armour <- 2 plate <- 2 ingot each
        var plan = Planner().Plan(new[] { Entry(26, 1) }, Armorer);

        Assert.Equal(new uint[] { 20, 23 }, plan.SubCrafts.Select(s => s.Recipe.RecipeId)); // ingots, then plates
        Assert.Equal(new[] { 4, 2 }, plan.SubCrafts.Select(s => s.Crafts));
        Assert.Equal(new[] { 2, 1 }, plan.SubCrafts.Select(s => s.Depth));
    }

    [Fact]
    public void ARecipeThatYieldsSeveral_IsRoundedUp_AndItsSparesAreNotMadeTwice()
    {
        _held[Ingot] = 10;
        var first = Entry(27, 1);  // 4 nails -> 2 crafts of 3 = 6, two spare
        var second = Entry(27, 1); // 4 more: 2 spare + 2 needed -> 1 craft

        var plan = Planner().Plan(new[] { first, second }, Carpenter);

        var nails = Assert.Single(plan.SubCrafts);
        Assert.Equal(3, nails.Crafts);
        Assert.Equal(new[] { first, second }, nails.For);
    }

    /// <summary>Two entries never both count on the same ore: the first in the list gets it.</summary>
    [Fact]
    public void OneBagCount_ServesTheWholeList_InListOrder()
    {
        _held[Ore] = 6; // two ingots' worth
        _held[Shard] = 50;
        var plateA = Entry(23, 1);
        var plateB = Entry(23, 1);

        var plan = Planner().Plan(new[] { plateA, plateB }, Armorer);

        Assert.Equal(1, plan.Achievable(plateA));
        Assert.Equal(0, plan.Achievable(plateB));
        Assert.Equal(2, Assert.Single(plan.SubCrafts).Crafts);

        var ore = Assert.Single(plan.Shortfalls);
        Assert.Equal((Ore, 6), (ore.ItemId, ore.Missing));
        Assert.Equal(6, ore.ByEntry[plateB]);
        Assert.False(ore.ByEntry.ContainsKey(plateA));
    }

    /// <summary>
    /// The safety rule. A trophy needs an ingot AND a rare drop; with no rare drop it cannot be made,
    /// so smelting its ingot would only burn ore that another entry — or the player — may want.
    /// </summary>
    [Fact]
    public void NothingIsCraftedTowardsAnEntry_ThatCannotBeFinishedAnyway()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        var trophy = Entry(25, 2);

        var plan = Planner().Plan(new[] { trophy }, Carpenter);

        Assert.Empty(plan.SubCrafts);
        Assert.Equal(0, plan.Achievable(trophy));
        var rare = Assert.Single(plan.Shortfalls);
        Assert.Equal((RareDrop, 2), (rare.ItemId, rare.Missing));
    }

    [Fact]
    public void AnEntryPartlyCovered_IsPlannedForThePartThatCanBeFinished()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        _held[RareDrop] = 4;
        var trophy = Entry(25, 10);

        var plan = Planner().Plan(new[] { trophy }, Carpenter);

        Assert.Equal(4, plan.Achievable(trophy));
        Assert.Equal(4, Assert.Single(plan.SubCrafts).Crafts);
        Assert.Equal(6, Assert.Single(plan.Shortfalls).Missing); // the rare drop only: the ore for the other six is there
    }

    /// <summary>The shortfall named is what is REALLY missing — the ore, not the ingot that ore would have made.</summary>
    [Fact]
    public void AShortfall_IsReportedAtTheBottomOfTheTree()
    {
        _held[Shard] = 50;
        var plate = Entry(23, 1);

        var plan = Planner().Plan(new[] { plate }, Armorer);

        var ore = Assert.Single(plan.Shortfalls);
        Assert.Equal((Ore, 6), (ore.ItemId, ore.Missing));
        Assert.Equal("item 1", ore.Name);
    }

    /// <summary>A caller that lists sub-crafts first means them to be used — they are not planned a second time.</summary>
    [Fact]
    public void WhatAnEarlierEntryMakes_FeedsTheEntriesAfterIt()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        _held[Lumber] = 3;

        var plan = Planner().Plan(new[] { Entry(20, 6), Entry(24, 3) }, Carpenter);

        Assert.Empty(plan.SubCrafts);
        Assert.Empty(plan.Shortfalls);
    }

    /// <summary>...but only AFTER it: listed the other way round, the shields need ingots before the ingot entry has run.</summary>
    [Fact]
    public void AnEntry_DoesNotCountOnOneListedAfterIt()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        _held[Lumber] = 3;

        var plan = Planner().Plan(new[] { Entry(24, 3), Entry(20, 6) }, Carpenter);

        Assert.Equal(6, Assert.Single(plan.SubCrafts).Crafts);
    }

    [Fact]
    public void AnIntermediateThisCharacterCannotMake_IsAShortfall_NotASubCraft()
    {
        _jobs.Remove(Blacksmith); // no gearset for it
        _held[Ore] = 50;
        _held[Shard] = 50;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer);

        Assert.Empty(plan.SubCrafts);
        Assert.Equal((Ingot, 2), (plan.Shortfalls[0].ItemId, plan.Shortfalls[0].Missing));
    }

    /// <summary>Automation only starts a synthesis it can expect to finish. (The player's own entries are taken at his word.)</summary>
    [Fact]
    public void ARecipeAboveTheJobsLevel_IsNotSubCrafted()
    {
        _held[Ore] = 50;

        _level = 59;
        var tooLow = Planner().Plan(new[] { Entry(51, 1) }, Armorer);
        Assert.Empty(tooLow.SubCrafts);
        Assert.Equal(MythrilIngot, Assert.Single(tooLow.Shortfalls).ItemId);

        _level = 60;
        var highEnough = Planner().Plan(new[] { Entry(51, 1) }, Armorer);
        Assert.Equal(50u, Assert.Single(highEnough.SubCrafts).Recipe.RecipeId);
    }

    [Fact]
    public void ABlockedSubCraft_IsNotPlannedAgain()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer, blockedRecipes: new uint[] { 20 });

        Assert.Empty(plan.SubCrafts);
        Assert.Equal(Ingot, Assert.Single(plan.Shortfalls).ItemId);
    }

    [Theory]
    [InlineData(Carpenter, 28u)]  // on Carpenter: its own wheel recipe, no gearset change
    [InlineData(Blacksmith, 29u)]
    [InlineData(Armorer, 28u)]    // neither: the first in the game's order
    public void AnIntermediateWithSeveralRecipes_UsesTheJobWeAreOn(uint currentJob, uint expectedRecipe)
    {
        _held[Log] = 5;

        var plan = Planner().Plan(new[] { Entry(30, 1) }, currentJob);

        Assert.Equal(expectedRecipe, Assert.Single(plan.SubCrafts).Recipe.RecipeId);
    }

    [Fact]
    public void AnIntermediateWithSeveralRecipes_SkipsTheOnesThisCharacterCannotUse()
    {
        _jobs.Remove(Carpenter);
        _held[Log] = 5;

        var plan = Planner().Plan(new[] { Entry(30, 1) }, Carpenter);

        Assert.Equal(29u, Assert.Single(plan.SubCrafts).Recipe.RecipeId);
    }

    /// <summary>Bad sheet data must end in a shortfall, not a stack overflow.</summary>
    [Fact]
    public void ARecipeCycle_Terminates()
    {
        var loop = Entry(40, 1);

        var plan = Planner().Plan(new[] { loop }, Carpenter);

        Assert.Equal(0, plan.Achievable(loop));
        Assert.NotEmpty(plan.Shortfalls);
    }

    [Fact]
    public void FinishedFlaggedAndUnknownEntries_AreIgnored()
    {
        var done = Entry(23, 2, crafted: 2);
        var flagged = Entry(23, 2);
        flagged.SkipReason = "short of ore";

        var plan = Planner().Plan(new[] { done, flagged, Entry(999, 1) }, Armorer);

        Assert.Empty(plan.SubCrafts);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public void CraftableNow_IsWhatTheBagsMake_WithoutAnySubCrafting()
    {
        _held[Ingot] = 5;
        _held[Lumber] = 9;
        var planner = Planner();

        Assert.Equal(2, planner.CraftableNow(AllRecipes.Single(r => r.RecipeId == 24))); // 2 ingots each
        Assert.Equal(0, planner.CraftableNow(AllRecipes.Single(r => r.RecipeId == 25))); // no rare drop
    }
}
