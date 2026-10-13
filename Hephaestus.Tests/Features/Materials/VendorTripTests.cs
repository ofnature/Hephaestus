using System.Numerics;
using Hephaestus.Features.Materials;

namespace Hephaestus.Tests.Features.Materials;

public class VendorTripTests
{
    private const uint Here = 130;
    private const uint Thal = 131;
    private const uint Ore = 5106;
    private const uint Honey = 4850;

    private static readonly Seller Aistan = new(1004419, 262157);
    private static readonly Seller Fridurih = new(1001966, 262158);
    private static readonly Seller Engerrand = new(1001204, 262159);

    private readonly FakeWorld _world = new();
    private DateTime _now = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    public VendorTripTests()
    {
        _world.Prices[Ore] = 2;
        _world.Prices[Honey] = 2;
    }

    private VendorTrip StartTrip(params FetchItem[] items)
    {
        var trip = new VendorTrip(_world);
        trip.Start(items, _now);
        return trip;
    }

    private static FetchItem Want(uint itemId, int target, int held = 0) => new(itemId, $"item {itemId}", target, target - held);

    private void RunToEnd(VendorTrip trip, int maxTicks = 2000)
    {
        for (var i = 0; i < maxTicks && trip.Active; i++)
        {
            _now = _now.AddSeconds(0.5);
            trip.Tick(_now);
        }

        Assert.False(trip.Active, "the trip never ended");
    }

    [Fact]
    public void ASellerStandingNearby_IsWalkedTo_AndBoughtFrom_UpToTheBagCount()
    {
        _world.Stand(Aistan, Here, new Vector3(20, 0, 0), stocks: Ore);
        _world.Bags[Ore] = 10;

        var trip = StartTrip(Want(Ore, target: 30, held: 10));
        RunToEnd(trip);

        Assert.Equal(30, _world.Bags[Ore]);
        Assert.Equal(new[] { (Ore, 20) }, _world.Purchases);
        Assert.Empty(trip.Notes);
        Assert.Equal("Bought everything.", trip.Status);
        Assert.Empty(_world.Teleports);
    }

    [Fact]
    public void AFarVendor_IsRiddenTo_AndDismountedFor()
    {
        _world.Stand(Aistan, Here, new Vector3(200, 0, 0), stocks: Ore);

        RunToEnd(StartTrip(Want(Ore, 3)));

        Assert.Equal(1, _world.Mounts);
        Assert.Equal(1, _world.Dismounts);
        Assert.False(_world.Mounted);
        Assert.Equal(3, _world.Bags[Ore]);
    }

    [Fact]
    public void ANearVendor_IsWalkedTo_OnFoot()
    {
        _world.Stand(Aistan, Here, new Vector3(30, 0, 0), stocks: Ore);

        RunToEnd(StartTrip(Want(Ore, 3)));

        Assert.Equal(0, _world.Mounts);
        Assert.Equal(3, _world.Bags[Ore]);
    }

    /// <summary>Indoors, or on cooldown: no mount, and no waiting for one either.</summary>
    [Fact]
    public void WhereMountingIsNotAllowed_ItWalks()
    {
        _world.Stand(Aistan, Here, new Vector3(200, 0, 0), stocks: Ore);
        _world.CanMount = false;

        RunToEnd(StartTrip(Want(Ore, 3)));

        Assert.Equal(0, _world.Mounts);
        Assert.Equal(3, _world.Bags[Ore]);
    }

    /// <summary>A shard right by a far vendor: hop across the city's aethernet instead of crossing it on foot.</summary>
    [Fact]
    public void AnInZoneAethernetShortcut_IsTaken_WhenTheWorldOffersOne()
    {
        _world.Stand(Aistan, Here, new Vector3(400, 0, 0), stocks: Ore);
        _world.Shortcut = destination => destination.X > 300 ? new TravelRoute(0, Here, 77, Here) : null;
        _world.HopLandsAt[77] = new Vector3(390, 0, 0);

        RunToEnd(StartTrip(Want(Ore, 3)));

        Assert.Equal(new uint[] { 77 }, _world.Hops);
        Assert.Empty(_world.Teleports);
        Assert.Equal(0, _world.Mounts); // 10 units left after the hop: not worth a mount
        Assert.Equal(3, _world.Bags[Ore]);
    }

    [Fact]
    public void MoreThan99_IsBoughtInSeveralTransactions()
    {
        _world.Stand(Aistan, Here, new Vector3(2, 0, 0), stocks: Ore);

        RunToEnd(StartTrip(Want(Ore, target: 250)));

        Assert.Equal(new[] { 99, 99, 52 }, _world.Purchases.Select(p => p.Count));
        Assert.Equal(250, _world.Bags[Ore]);
    }

    [Fact]
    public void ASellerInAnotherZone_IsReachedByTeleport()
    {
        _world.Stand(Aistan, Thal, new Vector3(-34, 14, 96), stocks: Ore);
        _world.Routes[Thal] = new TravelRoute(AetheryteId: 9, ArrivesIn: Thal, AethernetPlaceNameId: 0, Territory: Thal);

        RunToEnd(StartTrip(Want(Ore, target: 12)));

        Assert.Equal(new uint[] { 9 }, _world.Teleports);
        Assert.Empty(_world.Hops);
        Assert.Equal(12, _world.Bags[Ore]);
    }

    /// <summary>Ul'dah's Steps of Thal has no aetheryte of its own: teleport to Nald, then hop across the aethernet.</summary>
    [Fact]
    public void AZoneWithNoAetheryte_IsReachedByTeleportThenAethernetHop()
    {
        _world.Territory = 1; // somewhere else entirely
        _world.Stand(Aistan, Thal, new Vector3(-34, 14, 96), stocks: Ore);
        _world.Routes[Thal] = new TravelRoute(AetheryteId: 9, ArrivesIn: Here, AethernetPlaceNameId: 50, Territory: Thal);
        _world.HopLandsIn[50] = Thal;

        RunToEnd(StartTrip(Want(Ore, target: 12)));

        Assert.Equal(new uint[] { 9 }, _world.Teleports);
        Assert.Equal(new uint[] { 50 }, _world.Hops);
        Assert.Equal(12, _world.Bags[Ore]);
    }

    [Fact]
    public void ASellerNearby_BeatsOneThatNeedsATeleport()
    {
        _world.Stand(Aistan, Thal, new Vector3(0, 0, 0), stocks: Ore);
        _world.Routes[Thal] = new TravelRoute(9, Thal, 0, Thal);
        _world.Stand(Fridurih, Here, new Vector3(40, 0, 0), stocks: Ore);

        RunToEnd(StartTrip(Want(Ore, target: 5)));

        Assert.Empty(_world.Teleports);
        Assert.Equal(new[] { Fridurih.ShopId }, _world.ShopsOpened.Distinct());
    }

    [Fact]
    public void TheSellerWhoHasMostOfTheList_IsVisitedFirst_AndCoversIt()
    {
        _world.Stand(Aistan, Here, new Vector3(5, 0, 0), stocks: Ore);
        _world.Stand(Engerrand, Here, new Vector3(30, 0, 0), stocks: new[] { Ore, Honey });

        RunToEnd(StartTrip(Want(Ore, 3), Want(Honey, 3)));

        Assert.Equal(new[] { Engerrand.ShopId }, _world.ShopsOpened.Distinct());
        Assert.Equal((3, 3), (_world.Bags[Ore], _world.Bags[Honey]));
    }

    /// <summary>Placed in the data but not actually there (phased by a quest, say): strike them off, try the next.</summary>
    [Fact]
    public void ASellerWhoIsNotThere_IsStruckOff_AndTheNextOneIsTried()
    {
        // Aistan is placed in this zone (ranks above a teleport) but never spawns; Fridurih needs a teleport.
        _world.Stand(Aistan, Here, new Vector3(5, 0, 0), stocks: Ore);
        _world.Phased.Add(Aistan.NpcDataId);
        _world.Stand(Fridurih, Thal, new Vector3(60, 0, 0), stocks: Ore);
        _world.Routes[Thal] = new TravelRoute(9, Thal, 0, Thal);

        var trip = StartTrip(Want(Ore, target: 4));
        RunToEnd(trip);

        Assert.Equal(new Vector3(5, 0, 0), _world.Walks[0] - new Vector3(VendorTrip.Reach, 0, 0)); // Aistan first...
        Assert.Equal(new uint[] { 9 }, _world.Teleports);                                      // ...then the teleport
        Assert.Equal(new[] { Fridurih.ShopId }, _world.ShopsOpened.Distinct());
        Assert.Equal(4, _world.Bags[Ore]);
        Assert.Empty(trip.Notes);
    }

    [Fact]
    public void AnItemNobodySells_IsGivenUp_WithTheReason()
    {
        _world.Stand(Aistan, Here, new Vector3(5, 0, 0), stocks: Ore);

        var trip = StartTrip(Want(Ore, 2), Want(Honey, 2));
        RunToEnd(trip);

        Assert.Equal(2, _world.Bags[Ore]);
        Assert.Equal("no vendor sells it", trip.Notes[Honey]);
        Assert.Contains("1 item(s) not", trip.Status);
    }

    [Fact]
    public void RunningOutOfGil_BuysWhatItCan_ThenSaysSo()
    {
        _world.Stand(Aistan, Here, new Vector3(2, 0, 0), stocks: Ore);
        _world.Gil = 11; // five at 2 gil

        var trip = StartTrip(Want(Ore, target: 10));
        RunToEnd(trip);

        Assert.Equal(5, _world.Bags[Ore]);
        Assert.StartsWith("not enough gil", trip.Notes[Ore]);
    }

    [Fact]
    public void BagsFull_StopsTheTrip_AndNeedsAPerson()
    {
        _world.Stand(Aistan, Here, new Vector3(2, 0, 0), stocks: new[] { Ore, Honey });
        _world.FreeBagSlots = 0;

        var trip = StartTrip(Want(Ore, 5), Want(Honey, 5));
        RunToEnd(trip);

        Assert.True(trip.NeedsPerson);
        Assert.Equal("your bags are full", trip.Notes[Ore]);
        Assert.Equal("not bought: your bags are full", trip.Notes[Honey]);
        Assert.Single(_world.Purchases); // never a second try into full bags
    }

    /// <summary>The purchase call "worked" and nothing arrived: one transaction, then the item is given up — never a loop.</summary>
    [Fact]
    public void ATransactionThatLandsNothing_EndsThatItem()
    {
        _world.Stand(Aistan, Here, new Vector3(2, 0, 0), stocks: Ore);
        _world.PurchasesLand = false;

        var trip = StartTrip(Want(Ore, target: 5));
        RunToEnd(trip);

        Assert.Single(_world.Purchases);
        Assert.StartsWith("the vendor did not sell it", trip.Notes[Ore]);
        Assert.False(trip.NeedsPerson);
    }

    [Fact]
    public void WithNothingNearby_ItWaitsForVendorPositions_ThenUsesThem()
    {
        _world.Stand(Aistan, Thal, new Vector3(0, 0, 0), stocks: Ore);
        _world.Routes[Thal] = new TravelRoute(9, Thal, 0, Thal);
        _world.PlacesKnown = false;

        var trip = StartTrip(Want(Ore, 3));
        _now = _now.AddSeconds(1);
        trip.Tick(_now);
        Assert.Equal("Reading where vendors stand…", trip.Status);

        _world.PlacesKnown = true;
        RunToEnd(trip);
        Assert.Equal(3, _world.Bags[Ore]);
    }

    [Fact]
    public void ATeleportThatNeverLands_IsRetried_ThenTheSellerIsStruckOff()
    {
        _world.Territory = 1;
        _world.Stand(Aistan, Thal, new Vector3(0, 0, 0), stocks: Ore);
        _world.Routes[Thal] = new TravelRoute(9, Thal, 0, Thal);
        _world.TeleportsLand = false;

        var trip = StartTrip(Want(Ore, 3));
        RunToEnd(trip);

        Assert.Equal(VendorTrip.MaxAttempts, _world.Teleports.Count);
        Assert.Contains("the teleport never landed", trip.Notes[Ore]);
    }

    [Fact]
    public void WithoutANavigationPlugin_OnlyASellerWithinReachWorks()
    {
        _world.Stand(Aistan, Here, new Vector3(30, 0, 0), stocks: Ore);
        _world.CanWalk = false;

        var trip = StartTrip(Want(Ore, 3));
        RunToEnd(trip);

        Assert.Contains("no navigation plugin", trip.Notes[Ore]);
        Assert.Empty(_world.Purchases);
    }

    [Fact]
    public void Stop_StopsWalking_AndClosesAnOpenShop()
    {
        _world.Stand(Aistan, Here, new Vector3(2, 0, 0), stocks: Ore);
        var trip = StartTrip(Want(Ore, 3));
        for (var i = 0; i < 20 && _world.OpenShopId == 0; i++)
        {
            _now = _now.AddSeconds(0.5);
            trip.Tick(_now);
        }

        trip.Stop();

        Assert.False(trip.Active);
        Assert.Equal(0u, _world.OpenShopId);
        Assert.Equal("Stopped.", trip.Status);
    }

    /// <summary>A small simulated world: vendors stand where they are placed, walking and teleporting are instant.</summary>
    private sealed class FakeWorld : IVendorWorld
    {
        private readonly Dictionary<uint, (uint Territory, Vector3 Position)> _placed = new();
        private readonly Dictionary<uint, List<Seller>> _sellers = new();
        private readonly Dictionary<uint, HashSet<uint>> _stock = new();

        public Dictionary<uint, int> Bags { get; } = new();
        public Dictionary<uint, uint> Prices { get; } = new();
        public Dictionary<uint, TravelRoute> Routes { get; } = new();
        public Dictionary<uint, uint> HopLandsIn { get; } = new();
        public HashSet<uint> Phased { get; } = new();
        public List<uint> Teleports { get; } = new();
        public List<uint> Hops { get; } = new();
        public List<(uint Item, int Count)> Purchases { get; } = new();
        public List<uint> ShopsOpened { get; } = new();
        public List<Vector3> Walks { get; } = new();
        public bool TeleportsLand { get; set; } = true;
        public bool CanMount { get; set; } = true;
        public bool Mounted { get; private set; }
        public int Mounts { get; private set; }
        public int Dismounts { get; private set; }
        public Func<Vector3, TravelRoute?> Shortcut { get; set; } = _ => null;
        public Dictionary<uint, Vector3> HopLandsAt { get; } = new();
        public bool PurchasesLand { get; set; } = true;
        public uint OpenShopId { get; private set; }

        public uint Territory { get; set; } = Here;
        public bool Ready => OpenShopId == 0;
        public bool Travelling => false;
        public Vector3 PlayerPosition { get; private set; } = Vector3.Zero;
        public int FreeBagSlots { get; set; } = 50;
        public long Gil { get; set; } = 100_000;
        public bool PlacesKnown { get; set; } = true;
        public bool CanWalk { get; set; } = true;
        public bool Walking => false;

        public void Stand(Seller seller, uint territory, Vector3 at, params uint[] stocks)
        {
            _placed[seller.NpcDataId] = (territory, at);
            _stock[seller.ShopId] = stocks.ToHashSet();
            foreach (var item in stocks)
            {
                if (!_sellers.TryGetValue(item, out var list))
                    _sellers[item] = list = new List<Seller>();
                list.Add(seller);
            }
        }

        public int Held(uint itemId) => Bags.GetValueOrDefault(itemId);
        public uint PriceOf(uint itemId) => Prices.GetValueOrDefault(itemId);
        public IReadOnlyList<Seller> SellersOf(uint itemId) => _sellers.TryGetValue(itemId, out var s) ? s : new List<Seller>();

        public Vector3? SpawnedAt(uint npc)
            => _placed.TryGetValue(npc, out var at) && at.Territory == Territory && !Phased.Contains(npc) ? at.Position : null;

        public (uint Territory, Vector3 Position)? PlacedAt(uint npc) => PlacesKnown && _placed.TryGetValue(npc, out var at) ? at : null;

        public TravelRoute? RouteTo(uint territory, Vector3 near) => Routes.TryGetValue(territory, out var r) ? r : null;

        public bool Teleport(uint aetheryteId)
        {
            Teleports.Add(aetheryteId);
            var route = Routes.Values.First(r => r.AetheryteId == aetheryteId);
            if (TeleportsLand)
            {
                Territory = route.ArrivesIn;
                PlayerPosition = new Vector3(500, 0, 500);
            }

            return true;
        }

        public bool AethernetHop(uint placeNameId)
        {
            Hops.Add(placeNameId);
            if (HopLandsIn.TryGetValue(placeNameId, out var territory))
                Territory = territory;
            PlayerPosition = HopLandsAt.TryGetValue(placeNameId, out var at) ? at : new Vector3(400, 0, 400);
            return true;
        }

        public void Mount()
        {
            Mounts++;
            Mounted = CanMount;
        }

        public void Dismount()
        {
            Dismounts++;
            Mounted = false;
        }

        public TravelRoute? ShortcutTo(Vector3 destination) => Shortcut(destination);

        public void AbortTravel()
        {
        }

        public bool WalkTo(Vector3 destination, float within)
        {
            PlayerPosition = destination + new Vector3(within, 0, 0);
            Walks.Add(PlayerPosition);
            return true;
        }

        public void StopWalking()
        {
        }

        public bool OpenShop(uint npcDataId, uint shopId)
        {
            if (Mounted)
                return false; // the game will not talk to an NPC from the saddle
            if (SpawnedAt(npcDataId) is not { } at || Vector3.Distance(at, PlayerPosition) > VendorTrip.Reach + VendorTrip.ArrivalSlack)
                return false;

            OpenShopId = shopId;
            ShopsOpened.Add(shopId);
            return true;
        }

        public bool ShopOpen(uint shopId) => OpenShopId == shopId && shopId != 0;
        public bool ShopBusy(uint shopId) => false;

        public bool Buy(uint shopId, uint itemId, int count)
        {
            if (!_stock[shopId].Contains(itemId))
                return false;

            Purchases.Add((itemId, count));
            var cost = (long)count * PriceOf(itemId);
            if (PurchasesLand && FreeBagSlots > 0 && Gil >= cost)
            {
                Bags[itemId] = Held(itemId) + count;
                Gil -= cost;
            }

            return true;
        }

        public void CloseShop() => OpenShopId = 0;
    }
}
