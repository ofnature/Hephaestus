using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Hephaestus.Features.Materials;

/// <summary>An NPC that sells for gil, and which of their shops.</summary>
public readonly record struct Seller(uint NpcDataId, uint ShopId);

/// <summary>How to get into a zone from anywhere.</summary>
/// <param name="AetheryteId">Teleport here first; 0 = already on the right city aethernet, hop only.</param>
/// <param name="ArrivesIn">The territory that teleport lands in.</param>
/// <param name="AethernetPlaceNameId">Then hop across the city aethernet to this stop; 0 = no hop.</param>
/// <param name="Territory">Where the route ends.</param>
public readonly record struct TravelRoute(uint AetheryteId, uint ArrivesIn, uint AethernetPlaceNameId, uint Territory);

/// <summary>
/// Everything a vendor trip reads from and does to the game — the one seam, so the trip itself is
/// pure and tested (Odysseus's IStepWorld pattern). The adapter is Services/Game/VendorWorld.
/// </summary>
public interface IVendorWorld
{
    uint Territory { get; }

    /// <summary>Free to act: logged in, not zoning, not casting, not occupied, not crafting, not in combat.</summary>
    bool Ready { get; }

    /// <summary>A teleport or aethernet hop is under way (Lifestream busy, casting, zoning).</summary>
    bool Travelling { get; }

    Vector3 PlayerPosition { get; }

    int Held(uint itemId);

    int FreeBagSlots { get; }

    long Gil { get; }

    uint PriceOf(uint itemId);

    IReadOnlyList<Seller> SellersOf(uint itemId);

    /// <summary>Where the NPC stands right now, if they are spawned around the character.</summary>
    Vector3? SpawnedAt(uint npcDataId);

    /// <summary>Where the game data puts the NPC; null when it does not say.</summary>
    (uint Territory, Vector3 Position)? PlacedAt(uint npcDataId);

    /// <summary>The positions above have been read (false while they still load).</summary>
    bool PlacesKnown { get; }

    TravelRoute? RouteTo(uint territory, Vector3 near);

    bool Teleport(uint aetheryteId);

    bool AethernetHop(uint placeNameId);

    void AbortTravel();

    /// <summary>A navigation plugin (vnavmesh or Ariadne) is loaded and has a mesh for this zone.</summary>
    bool CanWalk { get; }

    bool Walking { get; }

    bool WalkTo(Vector3 destination, float within);

    void StopWalking();

    bool Mounted { get; }

    /// <summary>Mounting is allowed here and now (not indoors, not in combat, the action is off cooldown).</summary>
    bool CanMount { get; }

    void Mount();

    void Dismount();

    /// <summary>A hop across THIS zone's aethernet that beats walking to the point; null = just walk.</summary>
    TravelRoute? ShortcutTo(Vector3 destination);

    bool OpenShop(uint npcDataId, uint shopId);

    bool ShopOpen(uint shopId);

    bool ShopBusy(uint shopId);

    bool Buy(uint shopId, uint itemId, int count);

    void CloseShop();
}

/// <summary>
/// Buys a list of materials from NPC vendors — walking to them, and teleporting when none is near.
///
/// Where it goes, per stop (and it picks the next stop again after every one):
///   1. a seller SPAWNED around the character — found live in the object table, the same way
///      Odysseus buys (no data can go stale that way);
///   2. else a seller the game data places in THIS zone — walk there;
///   3. else a seller elsewhere with a route: teleport to the nearest attuned aetheryte in their
///      zone (via the city aethernet when the zone has none, e.g. Ul'dah's Steps of Thal).
/// Among equals, the seller covering the most of what is still owed wins, then the nearest.
/// Getting there: a hop across the zone's aethernet when a shard near the seller saves real walking
/// (the world does that sum), a mount for any walk over <see cref="MountDistance"/> where mounting is
/// allowed, and a dismount before talking. A hop or mount that does not happen costs nothing but
/// time: the trip walks from wherever it is.
///
/// How it buys (Odysseus's StepExecutor.TickPurchase pattern): the target is a BAG COUNT, never an
/// order — buy the shortfall, re-read the bag, buy again. At most 99 per transaction. A transaction
/// that lands nothing ends that item: never a loop. Bags full = a person-problem (NeedsPerson).
///
/// Failure is per SELLER: one that cannot be reached, walked to or opened is struck off and the next
/// best is tried; an item with no seller left is given up with the reason in <see cref="Notes"/>.
/// Every step has a pacing throttle, a deadline and a repeat cap.
/// </summary>
public sealed class VendorTrip
{
    public static readonly TimeSpan Pacing = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan PlacesWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan HopTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan WalkTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan WalkGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ShopOpenTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan BuySettle = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Talking distance, and the slack allowed on arrival (Odysseus's measured values).</summary>
    public const float Reach = 3f;
    public const float ArrivalSlack = 1.5f;

    /// <summary>The most one shop transaction takes.</summary>
    public const int MaxPerPurchase = 99;

    public const int MaxAttempts = 3;

    /// <summary>Walks longer than this are worth a mount (GBR's default for the same decision).</summary>
    public const float MountDistance = 40f;
    public static readonly TimeSpan MountWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DismountWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ShortcutGrace = TimeSpan.FromSeconds(3);

    private enum Step
    {
        Idle,
        Choosing,
        Teleporting,
        Hopping,
        Shortcut,
        Mounting,
        Walking,
        Dismounting,
        Opening,
        Buying,
        Closing,
    }

    private readonly IVendorWorld _world;
    private readonly Dictionary<uint, FetchItem> _owed = new();
    private readonly Dictionary<uint, string> _notes = new();
    private readonly Dictionary<Seller, string> _failedSellers = new();
    private readonly List<FetchItem> _stopItems = new();

    private Step _step;
    private DateTime _stepStarted;
    private DateTime _issuedAt;
    private DateTime _nextActionAt;
    private int _attempts;

    private Seller _seller;
    private Vector3 _destination;
    private TravelRoute? _route;

    private int _itemIndex;
    private int _heldBeforeBuy = -1;
    private int _buys;

    public VendorTrip(IVendorWorld world)
    {
        _world = world;
    }

    public bool Active => _step != Step.Idle;

    /// <summary>Stopped on something only a person can fix (bags full).</summary>
    public bool NeedsPerson { get; private set; }

    /// <summary>What it is doing, or how the last trip ended.</summary>
    public string Status { get; private set; } = "Idle — no vendor trip yet.";

    /// <summary>Why an item was not bought, by item id. Empty for items that were.</summary>
    public IReadOnlyDictionary<uint, string> Notes => _notes;

    /// <param name="items">What to buy: each item up to its <see cref="FetchItem.Target"/> bag count.</param>
    public void Start(IReadOnlyList<FetchItem> items, DateTime now)
    {
        _owed.Clear();
        _notes.Clear();
        _failedSellers.Clear();
        _stopItems.Clear();
        NeedsPerson = false;

        foreach (var item in items)
            _owed[item.ItemId] = item;

        Enter(Step.Choosing, now);
        Status = $"Buying {items.Count} material(s)";
    }

    public void Stop()
    {
        if (!Active)
            return;

        _world.StopWalking();
        if (_step is Step.Teleporting or Step.Hopping)
            _world.AbortTravel();
        if (_step is Step.Opening or Step.Buying or Step.Closing)
            _world.CloseShop();

        _step = Step.Idle;
        Status = "Stopped.";
    }

    public void Tick(DateTime now)
    {
        switch (_step)
        {
            case Step.Choosing:
                TickChoosing(now);
                break;
            case Step.Teleporting:
                TickTravel(now, hop: false);
                break;
            case Step.Hopping:
                TickTravel(now, hop: true);
                break;
            case Step.Shortcut:
                TickShortcut(now);
                break;
            case Step.Mounting:
                TickMounting(now);
                break;
            case Step.Walking:
                TickWalking(now);
                break;
            case Step.Dismounting:
                TickDismounting(now);
                break;
            case Step.Opening:
                TickOpening(now);
                break;
            case Step.Buying:
                TickBuying(now);
                break;
            case Step.Closing:
                TickClosing(now);
                break;
        }
    }

    // ---- choosing where to go ---------------------------------------------------------------------------------

    private void TickChoosing(DateTime now)
    {
        if (now < _nextActionAt)
            return;

        foreach (var done in _owed.Values.Where(i => _world.Held(i.ItemId) >= i.Target).ToList())
            _owed.Remove(done.ItemId);

        if (_owed.Count == 0)
        {
            Finish();
            return;
        }

        if (!_world.Ready)
        {
            if (now - _stepStarted > ReadyWait)
            {
                foreach (var item in _owed.Values)
                    _notes[item.ItemId] = "the character stayed busy (occupied, in combat or crafting)";
                _owed.Clear();
                Finish();
                return;
            }

            Status = "Waiting to be free to move";
            return;
        }

        if (ChooseVisit() is not { } stop)
        {
            // Nothing nearby, and the data that places the others is still being read: worth a wait.
            if (!_world.PlacesKnown && now - _stepStarted < PlacesWait)
            {
                Status = "Reading where vendors stand…";
                return;
            }

            foreach (var item in _owed.Values)
                _notes[item.ItemId] = WhyUnreachable(item.ItemId);
            _owed.Clear();
            Finish();
            return;
        }

        _seller = stop.Seller;
        _destination = stop.Destination;
        _route = stop.Route;
        _stopItems.Clear();
        _stopItems.AddRange(stop.Items);

        var names = string.Join(", ", _stopItems.Select(i => i.Name));
        if (_route is { } route)
        {
            Status = $"Travelling to a vendor for {names}";
            Enter(route.AetheryteId != 0 ? Step.Teleporting : Step.Hopping, now);
        }
        else if (_world.ShortcutTo(_destination) is { } shortcut)
        {
            _route = shortcut;
            Status = $"Taking the aethernet towards a vendor for {names}";
            Enter(Step.Shortcut, now);
        }
        else
        {
            Status = $"Walking to a vendor for {names}";
            EnterApproach(now);
        }
    }

    /// <summary>Walk, on a mount when the way is long and one is allowed.</summary>
    private void EnterApproach(DateTime now)
    {
        var destination = _world.SpawnedAt(_seller.NpcDataId) ?? _destination;
        var far = Vector3.Distance(_world.PlayerPosition, destination) > MountDistance;
        Enter(far && !_world.Mounted && _world.CanMount ? Step.Mounting : Step.Walking, now);
    }

    private sealed record Visit(Seller Seller, List<FetchItem> Items, int Rank, float Distance, Vector3 Destination, TravelRoute? Route);

    /// <summary>Spawned here > placed in this zone > placed elsewhere with a route; then most items covered; then nearest.</summary>
    private Visit? ChooseVisit()
    {
        var coverage = new Dictionary<Seller, List<FetchItem>>();
        foreach (var item in _owed.Values)
        {
            foreach (var seller in _world.SellersOf(item.ItemId))
            {
                if (_failedSellers.ContainsKey(seller))
                    continue;

                if (!coverage.TryGetValue(seller, out var items))
                    coverage[seller] = items = new List<FetchItem>();
                if (!items.Contains(item))
                    items.Add(item);
            }
        }

        Visit? best = null;
        var here = _world.PlayerPosition;
        foreach (var (seller, items) in coverage)
        {
            Visit candidate;
            if (_world.SpawnedAt(seller.NpcDataId) is { } spawned)
            {
                candidate = new Visit(seller, items, 0, Vector3.Distance(here, spawned), spawned, null);
            }
            else if (_world.PlacedAt(seller.NpcDataId) is { } placed)
            {
                if (placed.Territory == _world.Territory)
                    candidate = new Visit(seller, items, 1, Vector3.Distance(here, placed.Position), placed.Position, null);
                else if (_world.RouteTo(placed.Territory, placed.Position) is { } route)
                    candidate = new Visit(seller, items, 2, 0f, placed.Position, route);
                else
                    continue;
            }
            else
            {
                continue;
            }

            if (best == null
                || candidate.Rank < best.Rank
                || (candidate.Rank == best.Rank && candidate.Items.Count > best.Items.Count)
                || (candidate.Rank == best.Rank && candidate.Items.Count == best.Items.Count && candidate.Distance < best.Distance))
            {
                best = candidate;
            }
        }

        return best;
    }

    private string WhyUnreachable(uint itemId)
    {
        var sellers = _world.SellersOf(itemId);
        if (sellers.Count == 0)
            return "no vendor sells it";

        var failed = sellers.Where(_failedSellers.ContainsKey).Select(s => _failedSellers[s]).Distinct().ToList();
        return failed.Count > 0
            ? $"every vendor tried failed: {string.Join("; ", failed)}"
            : "no vendor for it is nearby, and none can be reached (not placed in the game data, or no attuned aetheryte near them)";
    }

    // ---- getting there ---------------------------------------------------------------------------------------

    private void TickTravel(DateTime now, bool hop)
    {
        var route = _route!.Value;
        var arrivesIn = hop ? route.Territory : route.ArrivesIn;

        if (_world.Territory == arrivesIn && _world.Ready && !_world.Travelling)
        {
            if (!hop && route.AethernetPlaceNameId != 0)
                Enter(Step.Hopping, now);
            else
                EnterApproach(now);
            return;
        }

        var timeout = hop ? HopTimeout : TeleportTimeout;
        if (now - _stepStarted > timeout * MaxAttempts)
        {
            _world.AbortTravel();
            FailSeller(hop ? "the aethernet hop never arrived" : "the teleport never landed", now);
            return;
        }

        // Under way (Lifestream walking to a shard, a cast, a loading screen), or issued and not yet due for a retry.
        if (_world.Travelling || !_world.Ready || (_issuedAt != default && now - _issuedAt < timeout))
            return;

        if (_attempts >= MaxAttempts)
        {
            FailSeller(hop ? "the aethernet hop never arrived" : "the teleport never landed", now);
            return;
        }

        _attempts++;
        _issuedAt = now;
        if (hop)
            _world.AethernetHop(route.AethernetPlaceNameId);
        else
            _world.Teleport(route.AetheryteId);
    }

    /// <summary>
    /// An in-zone aethernet hop (Lifestream walks to the nearest shard and uses it). Arrival cannot be
    /// read from the territory, so: give it a moment to begin, wait while it is under way, then walk
    /// from wherever that left us. A hop Lifestream refused simply means a longer walk.
    /// </summary>
    private void TickShortcut(DateTime now)
    {
        if (_issuedAt == default)
        {
            _issuedAt = now;
            _world.AethernetHop(_route!.Value.AethernetPlaceNameId);
            return;
        }

        if (now - _issuedAt < ShortcutGrace)
            return;

        if (_world.Travelling && now - _issuedAt < HopTimeout)
            return;

        _route = null;
        EnterApproach(now);
    }

    private void TickMounting(DateTime now)
    {
        if (_world.Mounted || now - _stepStarted > MountWait * MaxAttempts)
        {
            Enter(Step.Walking, now);
            return;
        }

        if (_issuedAt != default && now - _issuedAt < MountWait)
            return;

        if (_attempts >= MaxAttempts || !_world.CanMount)
        {
            Enter(Step.Walking, now); // on foot, then
            return;
        }

        _attempts++;
        _issuedAt = now;
        _world.Mount();
    }

    private void TickDismounting(DateTime now)
    {
        if (!_world.Mounted || now - _stepStarted > DismountWait)
        {
            Enter(Step.Opening, now);
            return;
        }

        if (_issuedAt == default || now - _issuedAt >= Pacing)
        {
            _issuedAt = now;
            _world.Dismount();
        }
    }

    private void TickWalking(DateTime now)
    {
        // Live position when they are spawned (it is exact); the data's position until then.
        var destination = _world.SpawnedAt(_seller.NpcDataId) ?? _destination;
        if (Vector3.Distance(_world.PlayerPosition, destination) <= Reach + ArrivalSlack)
        {
            _world.StopWalking();
            Enter(_world.Mounted ? Step.Dismounting : Step.Opening, now);
            return;
        }

        if (now - _stepStarted > WalkTimeout)
        {
            _world.StopWalking();
            FailSeller("could not walk to them in time", now);
            return;
        }

        if (!_world.CanWalk)
        {
            FailSeller("no navigation plugin (vnavmesh or Ariadne) is ready to walk there", now);
            return;
        }

        if (_world.Walking || (_issuedAt != default && now - _issuedAt < WalkGrace))
            return;

        if (_attempts >= MaxAttempts)
        {
            FailSeller("the path to them keeps stopping short", now);
            return;
        }

        _attempts++;
        _issuedAt = now;
        _world.WalkTo(destination, Reach);
    }

    // ---- the shop ---------------------------------------------------------------------------------------------

    private void TickOpening(DateTime now)
    {
        if (_world.ShopOpen(_seller.ShopId))
        {
            _itemIndex = 0;
            _heldBeforeBuy = -1;
            _buys = 0;
            Enter(Step.Buying, now);
            return;
        }

        if (now - _stepStarted > ShopOpenTimeout)
        {
            FailSeller("their shop never opened", now);
            return;
        }

        // Interacting can bounce off a moment of being occupied: retried, not issued once and hoped for.
        if (now >= _nextActionAt)
        {
            _nextActionAt = now + Pacing;
            _world.OpenShop(_seller.NpcDataId, _seller.ShopId);
        }
    }

    private void TickBuying(DateTime now)
    {
        if (!_world.ShopOpen(_seller.ShopId))
        {
            FailSeller("the shop window closed mid-purchase", now);
            return;
        }

        if (_itemIndex >= _stopItems.Count)
        {
            _world.CloseShop();
            Enter(Step.Closing, now);
            return;
        }

        var item = _stopItems[_itemIndex];
        var held = _world.Held(item.ItemId);

        if (held >= item.Target)
        {
            NextItem(item, note: null);
            return;
        }

        if (_world.ShopBusy(_seller.ShopId) || now < _nextActionAt)
            return;

        // A transaction was issued: wait for the bag to show it — then judge.
        if (_heldBeforeBuy >= 0)
        {
            if (held == _heldBeforeBuy && now - _issuedAt < BuySettle)
                return;

            if (held == _heldBeforeBuy)
            {
                if (_world.FreeBagSlots == 0)
                {
                    NeedsPerson = true;
                    _world.CloseShop();
                    _notes[item.ItemId] = "your bags are full";
                    foreach (var rest in _owed.Values.Where(i => i.ItemId != item.ItemId && _world.Held(i.ItemId) < i.Target))
                        _notes[rest.ItemId] = "not bought: your bags are full";
                    _owed.Clear();
                    Finish();
                    return;
                }

                NextItem(item, $"the vendor did not sell it (gil held: {_world.Gil:N0})");
                return;
            }

            _heldBeforeBuy = -1;
        }

        // Each transaction must land something, so this only guards against a runaway.
        if (_buys > ((item.Target + MaxPerPurchase - 1) / MaxPerPurchase) + 2)
        {
            NextItem(item, "stopped after too many purchases without reaching the count");
            return;
        }

        var count = Math.Min(item.Target - held, MaxPerPurchase);
        var price = _world.PriceOf(item.ItemId);
        if (price > 0)
            count = (int)Math.Min(count, _world.Gil / price);
        if (count <= 0)
        {
            NextItem(item, $"not enough gil ({_world.Gil:N0} held, {price:N0} each)");
            return;
        }

        if (!_world.Buy(_seller.ShopId, item.ItemId, count))
        {
            NextItem(item, "this vendor's shop does not stock it after all");
            return;
        }

        _buys++;
        _heldBeforeBuy = held;
        _issuedAt = now;
        _nextActionAt = now + Pacing;
        Status = $"Buying {count} × {item.Name}";
    }

    private void NextItem(FetchItem item, string? note)
    {
        if (note != null)
            _notes[item.ItemId] = note;
        _owed.Remove(item.ItemId);
        _itemIndex++;
        _heldBeforeBuy = -1;
        _buys = 0;
    }

    private void TickClosing(DateTime now)
    {
        if (!_world.ShopOpen(_seller.ShopId) || now - _stepStarted > CloseTimeout)
        {
            _nextActionAt = now + Pacing;
            Enter(Step.Choosing, now);
        }
    }

    // ---- bookkeeping --------------------------------------------------------------------------------------------

    private void FailSeller(string reason, DateTime now)
    {
        _failedSellers[_seller] = reason;
        _world.StopWalking();
        if (_world.ShopOpen(_seller.ShopId))
            _world.CloseShop();
        _nextActionAt = now + Pacing;
        Enter(Step.Choosing, now);
    }

    private void Enter(Step step, DateTime now)
    {
        _step = step;
        _stepStarted = now;
        _issuedAt = default;
        _attempts = 0;
    }

    private void Finish()
    {
        _step = Step.Idle;
        Status = NeedsPerson
            ? "Stopped buying: your bags are full."
            : _notes.Count == 0
                ? "Bought everything."
                : $"Bought what could be bought; {_notes.Count} item(s) not.";
    }
}
