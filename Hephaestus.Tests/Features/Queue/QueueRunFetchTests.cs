using Hephaestus.Features.Crafting;
using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

/// <summary>
/// The queue with automatic sourcing on (free trials by default): raw materials are bought, then
/// gathered, BEFORE any crafting — and every fetch is judged by the bags alone.
/// </summary>
public class QueueRunFetchTests
{
    private const uint Carpenter = 8;
    private const uint Blacksmith = 9;

    private const uint Ore = 1;
    private const uint Shard = 2;
    private const uint Log = 3;
    private const uint Ingot = 100;
    private const uint Lumber = 101;

    private const uint IngotRecipe = 20;
    private const uint LumberRecipe = 21;
    private const uint ShieldRecipe = 24;

    private static readonly RecipeInfo[] AllRecipes =
    {
        new(IngotRecipe, Blacksmith, "Bronze Ingot", Ingot, new RecipeInput[] { new(Ore, 3), new(Shard, 1) }),
        new(LumberRecipe, Carpenter, "Maple Lumber", Lumber, new RecipeInput[] { new(Log, 3) }),
        new(ShieldRecipe, Carpenter, "Maple Shield", 104, new RecipeInput[] { new(Ingot, 2), new(Lumber, 1) }),
    };

    private readonly Dictionary<uint, int> _held = new() { [Shard] = 99 };
    private readonly Dictionary<uint, uint> _prices = new() { [Ore] = 2 };
    private readonly HashSet<uint> _gatherable = new() { Log, Ore };
    private readonly Dictionary<uint, int> _retainerStock = new();
    private int _neverIndexed;
    private readonly Dictionary<uint, int> _chestStock = new();
    private bool _chestKnown = true;
    private DateTime _now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
    private bool _sourcing = true;

    private static RecipeInfo? Info(uint id) => AllRecipes.Where(r => r.RecipeId == id).Select(r => (RecipeInfo?)r).FirstOrDefault();

    private QueueRun Queue()
    {
        var planner = new MaterialPlanner(
            Info,
            item => AllRecipes.Where(r => r.ResultItemId == item).Select(r => new RecipeOption(r.RecipeId, r.JobId)).ToList(),
            _ => true,
            item => _held.GetValueOrDefault(item),
            item => item switch { Ore => "Copper Ore", Log => "Maple Log", _ => $"item {item}" });

        return new QueueRun(
            Info, _ => true, planner, () => true,
            () => _sourcing
                ? new MaterialSources(
                    new VendorSupply(item => _prices.GetValueOrDefault(item), 100_000),
                    new GatherSupply(_gatherable.Contains),
                    new RetainerSupply(item => _retainerStock.GetValueOrDefault(item), () => _neverIndexed),
                    new ChestSupply(item => _chestStock.GetValueOrDefault(item), () => _chestKnown))
                : null);
    }

    private static CraftList Shields(int quantity)
    {
        var list = new CraftList();
        list.Items.Add(new CraftListItem { RecipeId = ShieldRecipe, Quantity = quantity });
        return list;
    }

    private static QueueObservation Obs(
        uint job = Carpenter, bool logOpen = false, bool canSwitch = true, bool runActive = false, int runCompleted = 0,
        bool fetchActive = false, bool needsPerson = false, string fetchStatus = "", IReadOnlyDictionary<uint, string>? notes = null)
        => new(job, canSwitch, logOpen, runActive, runCompleted, CraftRunHalt.None, string.Empty, fetchActive, needsPerson, fetchStatus, notes);

    private QueueCommand Tick(QueueRun queue, QueueObservation obs)
    {
        _now = _now.AddSeconds(2);
        return queue.Tick(obs, _now);
    }

    /// <summary>A fetch that runs a moment and ends, with the bags as <paramref name="arrive"/> leaves them.</summary>
    private void FetchEnds(QueueRun queue, Action? arrive = null, bool needsPerson = false, string status = "", IReadOnlyDictionary<uint, string>? notes = null)
    {
        Tick(queue, Obs(fetchActive: true, fetchStatus: "under way"));
        arrive?.Invoke();
        Tick(queue, Obs(fetchActive: false, needsPerson: needsPerson, fetchStatus: status, notes: notes));
    }

    [Fact]
    public void RawMaterials_AreBoughtFirst_ForTheWholeList_BeforeAnyCrafting()
    {
        _held[Lumber] = 3;
        var queue = Queue();
        queue.Start(Shields(3), null);

        var command = Tick(queue, Obs());

        Assert.Equal(QueueCommandKind.Fetch, command.Kind);
        Assert.Equal(FetchKind.Buy, command.Fetch!.Kind);
        var ore = Assert.Single(command.Fetch.Items);
        Assert.Equal((Ore, 18, 18), (ore.ItemId, ore.Target, ore.Quantity)); // 6 ingots' worth
        Assert.Equal(QueuePhase.Fetching, queue.Phase);
        Assert.Same(command.Fetch, queue.CurrentFetch);
        Assert.Contains("Buying 18 × Copper Ore", queue.Status);

        FetchEnds(queue, () => _held[Ore] = 18);

        // Then the ingots — the sub-craft — on their own job.
        Assert.Equal(new QueueCommand(QueueCommandKind.SwitchJob, JobId: Blacksmith), Tick(queue, Obs()));
    }

    /// <summary>"You can always gather it if you can't buy it": what no vendor sells is gathered after the buying.</summary>
    [Fact]
    public void WhatIsNotSold_IsGathered_AfterTheBuying()
    {
        var queue = Queue();
        queue.Start(Shields(1), null);

        var buy = Tick(queue, Obs());
        Assert.Equal(FetchKind.Buy, buy.Fetch!.Kind);
        FetchEnds(queue, () => _held[Ore] = 6);

        var gather = Tick(queue, Obs());
        Assert.Equal(FetchKind.Gather, gather.Fetch!.Kind);
        Assert.Equal((Log, 3), (gather.Fetch.Items[0].ItemId, gather.Fetch.Items[0].Target));
        Assert.Contains("Asking Demeter to gather 3 × Maple Log", queue.Status);
    }

    [Fact]
    public void APurchaseThatDidNotArrive_IsGatheredInstead()
    {
        _held[Lumber] = 1;
        var queue = Queue();
        queue.Start(Shields(1), null);
        Tick(queue, Obs());

        FetchEnds(queue, arrive: null, status: "Bought what could be bought; 1 item(s) not.",
            notes: new Dictionary<uint, string> { [Ore] = "no vendor for it can be reached" });

        var next = Tick(queue, Obs());
        Assert.Equal(FetchKind.Gather, next.Fetch!.Kind);
        Assert.Equal(Ore, Assert.Single(next.Fetch.Items).ItemId);
    }

    /// <summary>Neither source delivered: the entry is flagged with what it is short of and why, and the queue moves on.</summary>
    [Fact]
    public void NothingArrivesFromEitherSource_TheEntryIsFlaggedWithBothReasons()
    {
        _held[Lumber] = 1;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);

        Tick(queue, Obs());
        FetchEnds(queue, notes: new Dictionary<uint, string> { [Ore] = "no vendor for it can be reached" });
        Tick(queue, Obs());
        FetchEnds(queue, notes: new Dictionary<uint, string> { [Ore] = "no node in range" });
        Tick(queue, Obs()); // flags the entry
        Tick(queue, Obs()); // nothing left: done

        Assert.False(queue.Active);
        Assert.Equal(
            "short of 6 × Copper Ore (not bought: no vendor for it can be reached; not gathered: no node in range)",
            list.Items[0].SkipReason);
    }

    [Fact]
    public void BagsFull_PausesTheQueue_ResumableAfterwards()
    {
        _held[Lumber] = 1;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);
        Tick(queue, Obs());

        FetchEnds(queue, needsPerson: true, status: "Stopped buying: your bags are full.");

        Assert.False(queue.Active);
        Assert.True(queue.CanResume);
        Assert.Equal("Paused — Stopped buying: your bags are full.", queue.Status);
        Assert.Null(list.Items[0].SkipReason);
    }

    [Fact]
    public void TheCraftingLog_IsClosed_BeforeGoingToFetch()
    {
        var queue = Queue();
        queue.Start(Shields(1), null);

        Assert.Equal(QueueCommandKind.CloseLog, Tick(queue, Obs(logOpen: true)).Kind);
        Assert.Equal(QueueCommandKind.None, Tick(queue, Obs(canSwitch: false)).Kind);
        Assert.Contains("Waiting to be free to move", queue.Status);
        Assert.Equal(QueueCommandKind.Fetch, Tick(queue, Obs()).Kind);
    }

    /// <summary>A fetcher that never reports itself active refused: everything asked of it is struck off that source.</summary>
    [Fact]
    public void AFetchThatNeverStarts_StrikesItsItemsOffThatSource()
    {
        _held[Lumber] = 1;
        var queue = Queue();
        queue.Start(Shields(1), null);
        Tick(queue, Obs());

        Tick(queue, Obs(fetchActive: false)); // still inside the start grace
        Tick(queue, Obs(fetchActive: false)); // past it: struck off

        var next = Tick(queue, Obs());
        Assert.Equal(FetchKind.Gather, next.Fetch!.Kind); // the purchase is off; gathering is next
    }

    /// <summary>Something keeps eating what is fetched (failing crafts, say): after a few rounds, stop fetching and name it.</summary>
    [Fact]
    public void AnItemFetchedAgainAndAgain_IsEventuallyStruckOff()
    {
        _held[Lumber] = 1;
        _gatherable.Clear();
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);

        for (var round = 0; round < QueueRun.MaxFetchesPerItem; round++)
        {
            Assert.Equal(QueueCommandKind.Fetch, Tick(queue, Obs()).Kind);
            FetchEnds(queue, () => _held[Ore] = 6);
            _held[Ore] = 0; // ...and gone again before it is used
        }

        Tick(queue, Obs()); // strikes it off
        Tick(queue, Obs()); // replans without it

        Assert.Contains($"not bought: fetched {QueueRun.MaxFetchesPerItem} times and still short", list.Items[0].SkipReason);
    }

    /// <summary>The source order: the company chest before the retainers, before anything is bought.</summary>
    [Fact]
    public void WhatTheChestHolds_IsTakenFirst_ThenTheRetainers()
    {
        _held[Lumber] = 1;
        _chestStock[Ore] = 3;
        _retainerStock[Ore] = 3;
        var queue = Queue();
        queue.Start(Shields(1), null);

        var first = Tick(queue, Obs());
        Assert.Equal(FetchKind.ChestWithdraw, first.Fetch!.Kind);
        Assert.Equal((Ore, 3, 3), (first.Fetch.Items[0].ItemId, first.Fetch.Items[0].Target, first.Fetch.Items[0].Quantity));
        Assert.Equal("Taking 3 × Copper Ore from the company chest", queue.Status);

        FetchEnds(queue, () => { _held[Ore] = 3; _chestStock[Ore] = 0; }); // the stacks left the chest

        var second = Tick(queue, Obs());
        Assert.Equal(FetchKind.Withdraw, second.Fetch!.Kind);
        Assert.Equal((Ore, 6, 3), (second.Fetch.Items[0].ItemId, second.Fetch.Items[0].Target, second.Fetch.Items[0].Quantity));
    }

    /// <summary>Nothing covers it and the chest has never been seen: one look in it, then a decision.</summary>
    [Fact]
    public void WhenStillShort_AnUnseenChest_IsLookedInOnce()
    {
        _held[Lumber] = 1;
        _prices.Clear();
        _gatherable.Clear();
        _chestKnown = false;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);

        var look = Tick(queue, Obs());
        Assert.Equal(FetchKind.ChestWithdraw, look.Fetch!.Kind);
        Assert.True(look.Fetch.Sweep);

        FetchEnds(queue, notes: new Dictionary<uint, string> { [Ore] = "none on the chest pages that could be read" });
        Tick(queue, Obs());
        Tick(queue, Obs());

        Assert.False(queue.Active);
        Assert.StartsWith("short of 6 × Copper Ore", list.Items[0].SkipReason);
    }

    /// <summary>The source order: what the retainers hold is fetched before anything is bought.</summary>
    [Fact]
    public void WhatRetainersHold_IsWithdrawnFirst_BeforeBuying()
    {
        _held[Lumber] = 1;
        _retainerStock[Ore] = 6;
        var queue = Queue();
        queue.Start(Shields(1), null);

        var command = Tick(queue, Obs());

        Assert.Equal(FetchKind.Withdraw, command.Fetch!.Kind);
        Assert.False(command.Fetch.Sweep);
        Assert.Equal((Ore, 6, 6), (command.Fetch.Items[0].ItemId, command.Fetch.Items[0].Target, command.Fetch.Items[0].Quantity));
        Assert.Equal("Withdrawing 6 × Copper Ore from retainers", queue.Status);

        FetchEnds(queue, () => _held[Ore] = 6);

        Assert.Equal(new QueueCommand(QueueCommandKind.SwitchJob, JobId: Blacksmith), Tick(queue, Obs())); // straight to the ingots
    }

    /// <summary>The index was stale: nothing came. The next source is tried, and that item is not asked of the retainers again.</summary>
    [Fact]
    public void AWithdrawalThatDidNotArrive_FallsThroughToBuying()
    {
        _held[Lumber] = 1;
        _retainerStock[Ore] = 6;
        var queue = Queue();
        queue.Start(Shields(1), null);
        Tick(queue, Obs());

        FetchEnds(queue, notes: new Dictionary<uint, string> { [Ore] = "the retainers visited did not have enough" });

        var next = Tick(queue, Obs());
        Assert.Equal(FetchKind.Buy, next.Fetch!.Kind);
    }

    /// <summary>Nothing known covers it, but retainers exist whose bags were never seen: one look, then a decision.</summary>
    [Fact]
    public void WhenStillShort_UnindexedRetainers_AreSweptOnce()
    {
        _held[Lumber] = 1;
        _prices.Clear();
        _gatherable.Clear();
        _neverIndexed = 2;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);

        var sweep = Tick(queue, Obs());
        Assert.Equal(FetchKind.Withdraw, sweep.Fetch!.Kind);
        Assert.True(sweep.Fetch.Sweep);
        Assert.Contains("Looking through unindexed retainers", queue.Status);

        FetchEnds(queue, notes: new Dictionary<uint, string> { [Ore] = "no retainer is known to hold it" });
        Tick(queue, Obs()); // flags: no second sweep
        Tick(queue, Obs());

        Assert.False(queue.Active);
        Assert.StartsWith("short of 6 × Copper Ore", list.Items[0].SkipReason);
    }

    [Fact]
    public void WithAutomaticSourcingOff_ThePlanUsesTheBagsAlone()
    {
        _sourcing = false;
        _held[Lumber] = 1;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);

        Assert.NotEqual(QueueCommandKind.Fetch, Tick(queue, Obs()).Kind); // flags it: nothing to fetch from
        Tick(queue, Obs());

        Assert.False(queue.Active);
        Assert.Equal("short of 6 × Copper Ore", list.Items[0].SkipReason);
    }

    /// <summary>Start or Resume is a person having looked: what failed before is tried again.</summary>
    [Fact]
    public void Resume_TriesAStruckOffSourceAgain()
    {
        _held[Lumber] = 1;
        _gatherable.Clear();
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);
        Tick(queue, Obs());
        FetchEnds(queue, notes: new Dictionary<uint, string> { [Ore] = "no vendor for it can be reached" });
        Tick(queue, Obs());
        Assert.NotNull(list.Items[0].SkipReason);

        list.Items[0].SkipReason = null;
        queue.Start(list, null);

        Assert.Equal(FetchKind.Buy, Tick(queue, Obs()).Fetch!.Kind);
    }
}
