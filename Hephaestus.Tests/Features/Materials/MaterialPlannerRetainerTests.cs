using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Materials;

/// <summary>Retainers as a source: right after the bags, BEFORE sub-crafting (the source order for paid accounts).</summary>
public class MaterialPlannerRetainerTests
{
    private const uint Blacksmith = 9;
    private const uint Armorer = 10;

    private const uint Ore = 1;
    private const uint Shard = 2;
    private const uint Ingot = 100;

    private static readonly RecipeInfo[] AllRecipes =
    {
        new(20, Blacksmith, "Bronze Ingot", Ingot, new RecipeInput[] { new(Ore, 3), new(Shard, 1) }),
        new(23, Armorer, "Bronze Plate", 103, new RecipeInput[] { new(Ingot, 2) }),
    };

    private readonly Dictionary<uint, int> _held = new() { [Shard] = 99 };
    private readonly Dictionary<uint, int> _retainers = new();

    private MaterialPlanner Planner() => new(
        id => AllRecipes.Where(r => r.RecipeId == id).Select(r => (RecipeInfo?)r).FirstOrDefault(),
        item => AllRecipes.Where(r => r.ResultItemId == item).Select(r => new RecipeOption(r.RecipeId, r.JobId)).ToList(),
        _ => true,
        item => _held.GetValueOrDefault(item),
        item => item == Ingot ? "Bronze Ingot" : item == Ore ? "Copper Ore" : $"item {item}");

    private readonly Dictionary<uint, int> _chest = new();

    private RetainerSupply Retainers() => new(item => _retainers.GetValueOrDefault(item), () => 0);

    private ChestSupply Chest() => new(item => _chest.GetValueOrDefault(item), () => true);

    private static CraftListItem Entry(uint recipeId, int quantity) => new() { RecipeId = recipeId, Quantity = quantity };

    [Fact]
    public void WhatARetainerHolds_IsWithdrawn_NotCraftedAgain()
    {
        _retainers[Ingot] = 2;
        _held[Ore] = 50;
        var plate = Entry(23, 1);

        var plan = Planner().Plan(new[] { plate }, Armorer, retainers: Retainers());

        var ingots = Assert.Single(plan.Withdrawals);
        Assert.Equal((Ingot, "Bronze Ingot", 2), (ingots.ItemId, ingots.Name, ingots.Quantity));
        Assert.Equal(2, ingots.ByEntry[plate]);
        Assert.Empty(plan.SubCrafts);
        Assert.Empty(plan.Shortfalls);
        Assert.Equal(1, plan.Achievable(plate));
    }

    [Fact]
    public void PartlyHeld_TheRestIsCrafted()
    {
        _retainers[Ingot] = 1;
        _held[Ore] = 50;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer, retainers: Retainers());

        Assert.Equal(1, Assert.Single(plan.Withdrawals).Quantity);
        Assert.Equal(1, Assert.Single(plan.SubCrafts).Crafts);
    }

    /// <summary>A retainer's two ingots serve one entry, not every entry that asks: the trial pass must not spend them twice.</summary>
    [Fact]
    public void RetainerStock_IsSpentOnce_AcrossTheList()
    {
        _retainers[Ingot] = 2;
        var first = Entry(23, 1);
        var second = Entry(23, 1);

        var plan = Planner().Plan(new[] { first, second }, Armorer, retainers: Retainers());

        var ingots = Assert.Single(plan.Withdrawals);
        Assert.Equal(2, ingots.Quantity);
        Assert.Equal(1, plan.Achievable(first));
        Assert.Equal(0, plan.Achievable(second));
        Assert.Equal((Ore, 6), (Assert.Single(plan.Shortfalls).ItemId, Assert.Single(plan.Shortfalls).Missing));
    }

    /// <summary>The bags come first: what is on hand is never fetched from a retainer.</summary>
    [Fact]
    public void TheBags_ComeBeforeTheRetainers()
    {
        _retainers[Ingot] = 5;
        _held[Ingot] = 2;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer, retainers: Retainers());

        Assert.Empty(plan.Withdrawals);
    }

    /// <summary>The source order: the chest before the retainers, both before smelting.</summary>
    [Fact]
    public void TheChest_ComesBeforeTheRetainers_AndBothBeforeCrafting()
    {
        _chest[Ingot] = 1;
        _retainers[Ingot] = 5;
        _held[Ore] = 50;
        var plate = Entry(23, 1);

        var plan = Planner().Plan(new[] { plate }, Armorer, retainers: Retainers(), chest: Chest());

        Assert.Equal((Ingot, 1), (Assert.Single(plan.ChestWithdrawals).ItemId, Assert.Single(plan.ChestWithdrawals).Quantity));
        Assert.Equal((Ingot, 1), (Assert.Single(plan.Withdrawals).ItemId, Assert.Single(plan.Withdrawals).Quantity));
        Assert.Empty(plan.SubCrafts);
        Assert.Equal(1, plan.Achievable(plate));
    }

    [Fact]
    public void WithoutRetainers_NothingIsWithdrawn()
    {
        _retainers[Ingot] = 5;

        var plan = Planner().Plan(new[] { Entry(23, 1) }, Armorer);

        Assert.Empty(plan.Withdrawals);
        Assert.Equal(Ore, Assert.Single(plan.Shortfalls).ItemId);
    }
}
