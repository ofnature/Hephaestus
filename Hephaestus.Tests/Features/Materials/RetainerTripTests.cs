using System.Numerics;
using Hephaestus.Features.Materials;

namespace Hephaestus.Tests.Features.Materials;

public class RetainerTripTests
{
    private const uint Here = 130;
    private const uint Limsa = 129;
    private const uint Ore = 5106;
    private const uint Honey = 4850;
    private const ulong Alpha = 1;
    private const ulong Beta = 2;
    private const ulong Gamma = 3;

    private readonly FakeWorld _world = new();
    private readonly RetainerIndex _index = new();
    private readonly List<(ulong Retainer, uint Item, int Nq, int Hq)> _withdrawn = new();
    private DateTime _now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    public RetainerTripTests()
    {
        _index.Know(Alpha, "Alpha");
        _index.Know(Beta, "Beta");
        _index.Know(Gamma, "Gamma");
        _world.Roster.AddRange(new[] { new RetainerSlot(Alpha, "Alpha", true), new RetainerSlot(Beta, "Beta", true), new RetainerSlot(Gamma, "Gamma", true) });
    }

    private RetainerTrip Trip() => new(
        _world,
        item => _index.Holders(item).Select(h => h.Retainer.RetainerId).ToList(),
        () => _index.NeverIndexed().Select(r => r.RetainerId).ToList(),
        id => _index.Retainers.TryGetValue(id, out var r) ? r.Name : $"retainer {id}",
        (retainer, item, nq, hq) => _withdrawn.Add((retainer, item, nq, hq)));

    private void Indexed(ulong retainer, params (uint Item, int Nq, int Hq)[] stacks)
    {
        var name = _index.Retainers[retainer].Name;
        _index.Learn(retainer, name, stacks.ToDictionary(s => s.Item, s => new ItemCount(s.Nq, s.Hq)), _now);
        _world.Stock(retainer, stacks);
    }

    private static FetchItem Want(uint itemId, int target, int held = 0) => new(itemId, $"item {itemId}", target, target - held);

    private RetainerTrip StartTrip(params FetchItem[] items) => Start(sweep: false, items);

    private RetainerTrip StartSweep(params FetchItem[] items) => Start(sweep: true, items);

    private RetainerTrip Start(bool sweep, FetchItem[] items)
    {
        var trip = Trip();
        trip.Start(items, sweep, _now);
        return trip;
    }

    private void RunToEnd(RetainerTrip trip, int maxTicks = 3000)
    {
        for (var i = 0; i < maxTicks && trip.Active; i++)
        {
            _now = _now.AddSeconds(0.5);
            trip.Tick(_now);
        }

        Assert.False(trip.Active, "the trip never ended");
    }

    [Fact]
    public void WithdrawsFromTheRetainerThatHoldsIt_UpToTheBagCount_HqFirst()
    {
        _world.Bell = new Vector3(10, 0, 0);
        Indexed(Alpha, (Ore, 40, 5));
        _world.Bags[Ore] = 2;

        var trip = StartTrip(Want(Ore, target: 20, held: 2));
        RunToEnd(trip);

        Assert.Equal(20, _world.Bags[Ore]);
        Assert.Equal(new[] { (Alpha, Ore, 0, 5), (Alpha, Ore, 13, 0) }, _withdrawn); // the HQ stack whole, then 13 NQ by quantity
        Assert.Equal(new[] { "withdraw", "quit" }, _world.MenuChoices);
        Assert.False(_world.BagsOpen);
        Assert.False(_world.RetainerListOpen);
        Assert.Empty(trip.Notes);
        Assert.Equal("Withdrew everything.", trip.Status);
    }

    [Fact]
    public void SeveralRetainers_AreVisitedInTurn_TheOneWithMoreOfTheListFirst()
    {
        _world.Bell = new Vector3(2, 0, 0);
        Indexed(Alpha, (Ore, 10, 0));
        Indexed(Beta, (Ore, 10, 0), (Honey, 10, 0));

        RunToEnd(StartTrip(Want(Ore, 15), Want(Honey, 5)));

        Assert.Equal(new[] { Beta, Alpha }, _world.Selected);
        Assert.Equal((15, 5), (_world.Bags[Ore], _world.Bags[Honey]));
        Assert.Equal(new[] { "withdraw", "quit", "withdraw", "quit" }, _world.MenuChoices);
    }

    [Fact]
    public void NoBellNearby_TeleportsToACityPlaza_ThenFindsOne()
    {
        _world.Territory = 1;
        _world.BellRoute = new TravelRoute(8, Limsa, 0, Limsa);
        _world.BellAfterTeleport = new Vector3(5, 0, 5);
        Indexed(Alpha, (Ore, 10, 0));

        RunToEnd(StartTrip(Want(Ore, 4)));

        Assert.Equal(new uint[] { 8 }, _world.Teleports);
        Assert.Equal(4, _world.Bags[Ore]);
    }

    [Fact]
    public void NoBell_AndNoRoute_GivesUpWithTheReason()
    {
        _world.Territory = 1;
        Indexed(Alpha, (Ore, 10, 0));

        var trip = StartTrip(Want(Ore, 4));
        RunToEnd(trip);

        Assert.Contains("no summoning bell nearby", trip.Notes[Ore]);
        Assert.Empty(_world.Selected);
    }

    [Fact]
    public void NothingIsKnownToHoldIt_NobodyIsVisited()
    {
        _world.Bell = new Vector3(2, 0, 0);
        Indexed(Alpha, (Honey, 10, 0));

        var trip = StartTrip(Want(Ore, 4));
        RunToEnd(trip);

        Assert.Empty(_world.Selected);
        Assert.Equal("no retainer is known to hold it", trip.Notes[Ore]);
    }

    /// <summary>A sweep opens the retainers never seen; what is owed is taken from wherever it turns up.</summary>
    [Fact]
    public void ASweep_VisitsTheNeverIndexed_AndTakesWhatItFinds()
    {
        _world.Bell = new Vector3(2, 0, 0);
        Indexed(Alpha, (Honey, 1, 0));
        _world.Stock(Gamma, (Ore, 30, 0)); // in Gamma's bags, unknown to the index

        RunToEnd(StartSweep(Want(Ore, 12)));

        Assert.Equal(new[] { Beta, Gamma }, _world.Selected); // the never-indexed, in roster order
        Assert.Equal(12, _world.Bags[Ore]);
    }

    [Fact]
    public void BagsFull_StopsTheTrip_AndNeedsAPerson()
    {
        _world.Bell = new Vector3(2, 0, 0);
        Indexed(Alpha, (Ore, 10, 0), (Honey, 10, 0));
        _world.FreeBagSlots = 0;

        var trip = StartTrip(Want(Ore, 4), Want(Honey, 4));
        RunToEnd(trip);

        Assert.True(trip.NeedsPerson);
        Assert.Equal("your bags are full", trip.Notes[Ore]);
        Assert.Equal("not withdrawn: your bags are full", trip.Notes[Honey]);
        Assert.False(_world.BagsOpen);
        Assert.False(_world.RetainerListOpen);
    }

    [Fact]
    public void ARetainerNoLongerInTheList_IsSkipped()
    {
        _world.Bell = new Vector3(2, 0, 0);
        Indexed(Alpha, (Ore, 10, 0));
        _world.Roster.RemoveAll(r => r.Id == Alpha);

        var trip = StartTrip(Want(Ore, 4));
        RunToEnd(trip);

        Assert.Empty(_world.Selected);
        Assert.Equal("no retainer is known to hold it", trip.Notes[Ore]);
    }

    [Fact]
    public void Stop_ClosesWhatIsOpen()
    {
        _world.Bell = new Vector3(2, 0, 0);
        Indexed(Alpha, (Ore, 10, 0));
        var trip = StartTrip(Want(Ore, 4));
        for (var i = 0; i < 40 && !_world.BagsOpen; i++)
        {
            _now = _now.AddSeconds(0.5);
            trip.Tick(_now);
        }

        Assert.True(_world.BagsOpen);
        trip.Stop();

        Assert.False(trip.Active);
        Assert.False(_world.BagsOpen);
        Assert.False(_world.RetainerListOpen);
    }

    /// <summary>A bell and retainers, simulated: menus open in the order the game opens them, withdrawals land at once.</summary>
    private sealed class FakeWorld : IRetainerWorld
    {
        private readonly Dictionary<ulong, Dictionary<(uint Item, bool Hq), int>> _stock = new();
        private ulong _open;

        public Dictionary<uint, int> Bags { get; } = new();
        public List<RetainerSlot> Roster { get; } = new();
        public List<ulong> Selected { get; } = new();
        public List<string> MenuChoices { get; } = new();
        public List<uint> Teleports { get; } = new();
        public Vector3? Bell { get; set; }
        public Vector3? BellAfterTeleport { get; set; }
        public TravelRoute? BellRoute { get; set; }
        public uint Territory { get; set; } = Here;
        public bool Ready => !BagsOpen && !MenuOpen;
        public bool Travelling => false;
        public Vector3 PlayerPosition { get; private set; }
        public int FreeBagSlots { get; set; } = 50;
        public bool AtABell => RetainerListOpen;
        public bool CanWalk => true;
        public bool Walking => false;
        public bool Mounted { get; private set; }
        public bool CanMount => true;
        public bool RetainerListOpen { get; private set; }
        public bool MenuOpen { get; private set; }
        public bool BagsOpen { get; private set; }
        public bool NumericOpen { get; private set; }
        private (uint Item, bool Hq, int Count)? _prompt;

        IReadOnlyList<RetainerSlot> IRetainerWorld.Roster => Roster;

        public void Stock(ulong retainer, params (uint Item, int Nq, int Hq)[] stacks)
        {
            var bags = _stock[retainer] = new Dictionary<(uint, bool), int>();
            foreach (var (item, nq, hq) in stacks)
            {
                if (nq > 0) bags[(item, false)] = nq;
                if (hq > 0) bags[(item, true)] = hq;
            }
        }

        public int Held(uint itemId) => Bags.GetValueOrDefault(itemId);
        public Vector3? NearestBell() => Bell;
        public TravelRoute? RouteToABell() => BellRoute;

        public bool Teleport(uint aetheryteId)
        {
            Teleports.Add(aetheryteId);
            Territory = BellRoute!.Value.ArrivesIn;
            PlayerPosition = new Vector3(200, 0, 200);
            Bell = BellAfterTeleport;
            return true;
        }

        public bool AethernetHop(uint placeNameId) => true;
        public void AbortTravel() { }
        public bool WalkTo(Vector3 destination, float within) { PlayerPosition = destination + new Vector3(within, 0, 0); return true; }
        public void StopWalking() { }
        public void Mount() => Mounted = true;
        public void Dismount() => Mounted = false;

        public bool InteractWithBell()
        {
            if (Bell is not { } bell || Vector3.Distance(bell, PlayerPosition) > RetainerTrip.Reach + RetainerTrip.ArrivalSlack || Mounted)
                return false;
            RetainerListOpen = true;
            return true;
        }

        public bool AdvanceTalk() => false;

        public bool SelectRetainer(ulong retainerId)
        {
            if (!RetainerListOpen || Roster.All(r => r.Id != retainerId))
                return false;
            Selected.Add(retainerId);
            _open = retainerId;
            MenuOpen = true;
            return true;
        }

        public bool ChooseMenuEntry(string containing, int fallbackIndex)
        {
            if (!MenuOpen)
                return false;
            MenuChoices.Add(containing);
            MenuOpen = false;
            if (containing == "withdraw")
                BagsOpen = true;
            return true;
        }

        public ItemCount InRetainerBags(uint itemId)
        {
            var bags = _stock.GetValueOrDefault(_open);
            return bags == null ? new ItemCount(0, 0) : new ItemCount(bags.GetValueOrDefault((itemId, false)), bags.GetValueOrDefault((itemId, true)));
        }

        public int Retrieve(uint itemId, bool hq, int count, out bool asksQuantity)
        {
            asksQuantity = false;
            if (!BagsOpen || !_stock.TryGetValue(_open, out var bags) || !bags.TryGetValue((itemId, hq), out var stack) || stack == 0)
                return 0;
            if (stack <= count)
            {
                Move(itemId, hq, stack);
                return stack;
            }

            asksQuantity = true;
            NumericOpen = true;
            _prompt = (itemId, hq, count);
            return count;
        }

        public bool EnterQuantity(int quantity)
        {
            if (!NumericOpen || _prompt is not { } prompt)
                return false;
            NumericOpen = false;
            _prompt = null;
            Move(prompt.Item, prompt.Hq, Math.Min(quantity, prompt.Count));
            return true;
        }

        private void Move(uint itemId, bool hq, int count)
        {
            if (FreeBagSlots == 0)
                return; // the game refuses; nothing moves
            _stock[_open][(itemId, hq)] -= count;
            Bags[itemId] = Held(itemId) + count;
        }

        public void CloseBags()
        {
            BagsOpen = false;
            MenuOpen = true; // back to the retainer's menu
        }

        public void CloseRetainerList() => RetainerListOpen = false;
    }
}
