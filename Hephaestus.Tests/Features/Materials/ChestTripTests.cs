using System.Numerics;
using Hephaestus.Features.Materials;

namespace Hephaestus.Tests.Features.Materials;

public class ChestTripTests
{
    private const uint Ore = 5106;
    private const uint Honey = 4850;

    private readonly FakeWorld _world = new();
    private DateTime _now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private static FetchItem Want(uint itemId, int target, int held = 0) => new(itemId, $"item {itemId}", target, target - held);

    private ChestTrip StartTrip(params FetchItem[] items) => Start(false, items);

    private ChestTrip StartSweep(params FetchItem[] items) => Start(true, items);

    private ChestTrip Start(bool sweep, FetchItem[] items)
    {
        var trip = new ChestTrip(_world);
        trip.Start(items, sweep, _now);
        return trip;
    }

    private void RunToEnd(ChestTrip trip, int maxTicks = 3000)
    {
        for (var i = 0; i < maxTicks && trip.Active; i++)
        {
            _now = _now.AddSeconds(0.25);
            trip.Tick(_now);
        }

        Assert.False(trip.Active, "the trip never ended");
    }

    [Fact]
    public void TakesTheSmallestStackThatCovers_ElseTheLargest_UntilCovered()
    {
        _world.Chest = new Vector3(5, 0, 0);
        _world.Stock(1, (Ore, 99), (Ore, 10), (Ore, 30));

        var trip = StartTrip(Want(Ore, target: 25));
        RunToEnd(trip);

        Assert.Equal(25, _world.Bags[Ore]); // the 30 split down to exactly 25
        Assert.Equal(1, trip.StacksMoved);
        Assert.Empty(trip.Notes);
        Assert.False(_world.ChestOpen);

        _world.Stock(1, (Ore, 10), (Ore, 8));
        _world.Bags.Clear();
        var second = StartTrip(Want(Ore, target: 15));
        RunToEnd(second);

        Assert.Equal(15, _world.Bags[Ore]); // nothing covers 15 alone: 10 whole, then the 8 split to 5
        Assert.Equal(2, second.StacksMoved);
    }

    /// <summary>Pages only load once their tab is viewed: every tab is clicked before anything is judged absent.</summary>
    [Fact]
    public void EveryTab_IsViewedFirst_SoAStackOnPageFiveIsFound()
    {
        _world.Chest = new Vector3(2, 0, 0);
        _world.Stock(5, (Honey, 12));

        RunToEnd(StartTrip(Want(Honey, 6)));

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, _world.Viewed);
        Assert.Equal(6, _world.Bags[Honey]); // 12 split to the 6 that were short
    }

    [Fact]
    public void NoChestNearby_GoesToTheEstate_ThenFindsIt()
    {
        _world.ChestAtEstate = new Vector3(8, 0, 8);
        _world.Stock(2, (Ore, 20));

        var trip = StartTrip(Want(Ore, 4));
        RunToEnd(trip);

        Assert.Equal(1, _world.EstateTrips);
        Assert.Equal(4, _world.Bags[Ore]); // 20 split to 4
        Assert.Empty(trip.Notes);
    }

    [Fact]
    public void NoChest_AndNoLifestream_GivesUpWithTheReason()
    {
        _world.LifestreamLoaded = false;

        var trip = StartTrip(Want(Ore, 4));
        RunToEnd(trip);

        Assert.Contains("Lifestream is not loaded", trip.Notes[Ore]);
        Assert.Equal(0, _world.EstateTrips);
    }

    [Fact]
    public void NothingOnAnyPage_IsNotedAsUnreadable_NeverAsAbsent()
    {
        _world.Chest = new Vector3(2, 0, 0);
        _world.Stock(1, (Honey, 3));

        var trip = StartTrip(Want(Ore, 4));
        RunToEnd(trip);

        Assert.Equal("none on the chest pages that could be read", trip.Notes[Ore]);
        Assert.False(_world.ChestOpen);
    }

    [Fact]
    public void BagsFull_StopsTheTrip_AndNeedsAPerson()
    {
        _world.Chest = new Vector3(2, 0, 0);
        _world.Stock(1, (Ore, 20), (Honey, 20));
        _world.FreeBagSlots = 0;

        var trip = StartTrip(Want(Ore, 4), Want(Honey, 4));
        RunToEnd(trip);

        Assert.True(trip.NeedsPerson);
        Assert.Equal("your bags are full", trip.Notes[Ore]);
        Assert.Equal("not taken: your bags are full", trip.Notes[Honey]);
        Assert.False(_world.ChestOpen);
    }

    /// <summary>A move the game accepted but never carried out is one stack given up on, not a loop.</summary>
    [Fact]
    public void AStackThatNeverLeaves_IsGivenUpOn()
    {
        _world.Chest = new Vector3(2, 0, 0);
        _world.Stock(1, (Ore, 20));
        _world.MovesLand = false;

        var trip = StartTrip(Want(Ore, 4));
        RunToEnd(trip);

        Assert.Equal("a stack did not leave the chest", trip.Notes[Ore]);
        Assert.Equal(1, _world.Moves);
    }

    [Fact]
    public void ASweep_OpensAndViewsTheChest_EvenWhenNothingIsExpected()
    {
        _world.Chest = new Vector3(2, 0, 0);
        _world.Stock(3, (Ore, 5));

        RunToEnd(StartSweep(Want(Ore, 4)));

        Assert.Equal(5, _world.Viewed.Count);
        Assert.Equal(4, _world.Bags[Ore]); // 5 split to 4
    }

    [Fact]
    public void Stop_ClosesTheChest()
    {
        _world.Chest = new Vector3(2, 0, 0);
        _world.Stock(1, (Ore, 20));
        var trip = StartTrip(Want(Ore, 4));
        for (var i = 0; i < 60 && !_world.ChestOpen; i++)
        {
            _now = _now.AddSeconds(0.25);
            trip.Tick(_now);
        }

        Assert.True(_world.ChestOpen);
        trip.Stop();

        Assert.False(trip.Active);
        Assert.False(_world.ChestOpen);
    }

    /// <summary>A covering stack is split to the exact shortfall, leaving the rest for the fleet.</summary>
    [Fact]
    public void ACoveringStack_IsSplitToTheExactShortfall_NotTakenWhole()
    {
        _world.Chest = new Vector3(2, 0, 0);
        _world.Stock(1, (Ore, 99));

        var trip = StartTrip(Want(Ore, 6));
        RunToEnd(trip);

        Assert.Equal(6, _world.Bags[Ore]); // exactly what was short, not the whole 99
        Assert.Equal(1, trip.StacksMoved);
        Assert.Empty(trip.Notes);
    }

    /// <summary>The game's "how many?" prompt never appearing is one stack given up on, nothing moved.</summary>
    [Fact]
    public void APromptThatNeverAppears_IsGivenUpOn()
    {
        _world.Chest = new Vector3(2, 0, 0);
        _world.Stock(1, (Ore, 20));
        _world.PromptAppears = false;

        var trip = StartTrip(Want(Ore, 4));
        RunToEnd(trip);

        Assert.Equal("the chest never asked how many — nothing moved", trip.Notes[Ore]);
        Assert.Equal(0, _world.Held(Ore));
        Assert.Equal(1, _world.Moves);
    }

    /// <summary>A chest and an estate, simulated: pages load when viewed, and a split waits on the prompt.</summary>
    private sealed class FakeWorld : IChestWorld
    {
        private readonly Dictionary<int, List<(uint Item, int Qty)>> _pages = new();
        private readonly HashSet<int> _loaded = new();
        private ChestStack? _pending;
        private int _pendingAmount;
        private bool _promptUp;

        public Dictionary<uint, int> Bags { get; } = new();
        public List<int> Viewed { get; } = new();
        public Vector3? Chest { get; set; }
        public Vector3? ChestAtEstate { get; set; }
        public bool LifestreamLoaded { get; set; } = true;
        public bool MovesLand { get; set; } = true;
        public bool PromptAppears { get; set; } = true;
        public int EstateTrips { get; private set; }
        public int Moves { get; private set; }
        public int FreeBagSlots { get; set; } = 50;
        public bool Ready => !ChestOpen;
        public bool Travelling => false;
        public Vector3 PlayerPosition { get; private set; }
        public bool CanGoToEstate => LifestreamLoaded;
        public bool CanWalk => true;
        public bool Walking => false;
        public bool Mounted { get; private set; }
        public bool CanMount => true;
        public bool ChestOpen { get; private set; }
        public bool QuantityPromptOpen => _promptUp;

        public void Stock(int page, params (uint Item, int Qty)[] stacks) => _pages[page] = stacks.ToList();

        public int Held(uint itemId) => Bags.GetValueOrDefault(itemId);
        public Vector3? NearestChest() => Chest;

        public bool GoToEstate()
        {
            EstateTrips++;
            Chest = ChestAtEstate;
            PlayerPosition = new Vector3(100, 0, 100);
            return true;
        }

        public void AbortTravel() { }
        public bool WalkTo(Vector3 destination, float within) { PlayerPosition = destination + new Vector3(within, 0, 0); return true; }
        public void StopWalking() { }
        public void Mount() => Mounted = true;
        public void Dismount() => Mounted = false;

        public bool InteractWithChest()
        {
            if (Chest is not { } chest || Vector3.Distance(chest, PlayerPosition) > ChestTrip.Reach + ChestTrip.ArrivalSlack || Mounted)
                return false;
            ChestOpen = true;
            return true;
        }

        public bool AdvanceTalk() => false;
        public bool PageLoaded(int page) => _loaded.Contains(page);

        public bool ViewPage(int page)
        {
            if (!ChestOpen)
                return false;
            Viewed.Add(page);
            _loaded.Add(page);
            return true;
        }

        public IReadOnlyList<ChestStack> ChestStacks(uint itemId)
        {
            var stacks = new List<ChestStack>();
            foreach (var (page, list) in _pages)
            {
                if (!_loaded.Contains(page))
                    continue;
                for (var slot = 0; slot < list.Count; slot++)
                {
                    if (list[slot].Item == itemId && list[slot].Qty > 0)
                        stacks.Add(new ChestStack(page, slot, itemId, list[slot].Qty, false));
                }
            }

            return stacks;
        }

        public bool Withdraw(ChestStack stack)
        {
            if (!ChestOpen || FreeBagSlots == 0)
                return false;
            Moves++;
            if (MovesLand)
                Apply(stack, stack.Quantity); // a whole move happens at once, no prompt
            return true;
        }

        public bool WithdrawSome(ChestStack stack, int amount)
        {
            if (!ChestOpen || FreeBagSlots == 0)
                return false;
            Moves++;
            _pending = stack;
            _pendingAmount = amount;
            _promptUp = PromptAppears;
            return true; // the move lands when the prompt is answered
        }

        public void AnswerQuantity(int amount)
        {
            _promptUp = false;
            if (MovesLand && _pending is { } stack)
                Apply(stack, _pendingAmount);
            _pending = null;
        }

        public int QuantityAt(ChestStack stack)
        {
            if (!_pages.TryGetValue(stack.Page, out var list) || stack.Slot >= list.Count)
                return 0;
            return list[stack.Slot].Item == stack.ItemId ? list[stack.Slot].Qty : 0;
        }

        private void Apply(ChestStack stack, int amount)
        {
            var list = _pages[stack.Page];
            list[stack.Slot] = (stack.ItemId, list[stack.Slot].Qty - amount);
            Bags[stack.ItemId] = Held(stack.ItemId) + amount;
        }

        public void CloseChest() => ChestOpen = false;
    }
}
