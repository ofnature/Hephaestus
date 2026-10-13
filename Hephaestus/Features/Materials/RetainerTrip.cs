using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Hephaestus.Features.Materials;

/// <summary>A retainer as the game lists them right now.</summary>
public readonly record struct RetainerSlot(ulong Id, string Name, bool Available);

/// <summary>
/// Everything a retainer trip reads from and does to the game — the one seam, so the trip is pure
/// and tested. The adapter is Services/Game/RetainerWorld.
/// </summary>
public interface IRetainerWorld
{
    uint Territory { get; }

    /// <summary>Free to act: logged in, not zoning, not casting, not occupied, not crafting, not in combat.</summary>
    bool Ready { get; }

    bool Travelling { get; }

    Vector3 PlayerPosition { get; }

    int Held(uint itemId);

    int FreeBagSlots { get; }

    /// <summary>Already at a bell — ours, the player's, or another plugin's.</summary>
    bool AtABell { get; }

    /// <summary>The nearest targetable Summoning Bell spawned around the character.</summary>
    Vector3? NearestBell();

    /// <summary>A teleport to a city plaza that has a bell by its aetheryte.</summary>
    TravelRoute? RouteToABell();

    bool Teleport(uint aetheryteId);

    bool AethernetHop(uint placeNameId);

    void AbortTravel();

    bool CanWalk { get; }

    bool Walking { get; }

    bool WalkTo(Vector3 destination, float within);

    void StopWalking();

    bool Mounted { get; }

    bool CanMount { get; }

    void Mount();

    void Dismount();

    bool InteractWithBell();

    /// <summary>A subtitle box was up and has been clicked.</summary>
    bool AdvanceTalk();

    bool RetainerListOpen { get; }

    IReadOnlyList<RetainerSlot> Roster { get; }

    /// <summary>Pick a retainer in the open list. False = not in it.</summary>
    bool SelectRetainer(ulong retainerId);

    /// <summary>The retainer's own menu (a SelectString) is up.</summary>
    bool MenuOpen { get; }

    /// <summary>Choose the entry whose text contains <paramref name="containing"/>; else the fallback index (-1 = the last).</summary>
    bool ChooseMenuEntry(string containing, int fallbackIndex);

    /// <summary>The retainer's bags are open and every page is loaded.</summary>
    bool BagsOpen { get; }

    ItemCount InRetainerBags(uint itemId);

    /// <summary>
    /// Tell the game to hand over a stack of the item: the whole stack when it holds no more than
    /// <paramref name="count"/>, else <paramref name="count"/> of it through the quantity prompt.
    /// </summary>
    /// <returns>How many that will bring, 0 when there is no such stack.</returns>
    int Retrieve(uint itemId, bool hq, int count, out bool asksQuantity);

    bool NumericOpen { get; }

    bool EnterQuantity(int quantity);

    void CloseBags();

    void CloseRetainerList();
}

/// <summary>
/// Withdraws materials from the character's retainers at a Summoning Bell — the retainer source
/// (paid accounts; before sub-crafting in the source order).
///
/// Where it goes: the nearest bell spawned around the character; none in reach, a teleport to a
/// city plaza with one. Whom it visits: whoever the index says holds what is owed, most items
/// first; on a SWEEP also every retainer never indexed, whose bags the watcher then learns. What it
/// takes: at each retainer, the LIVE bags decide (the index only chose the visit) — HQ before NQ,
/// each transaction verified by the character's own bag count, never by what the game said.
/// The flow is GBR's Vulcan, which proved it: interact, Talk, RetainerList, the retainer's menu
/// ("Entrust or withdraw items"), bags, Retrieve / Retrieve Quantity + the numeric prompt, close,
/// "Quit", next. Bags full = a person-problem (NeedsPerson). Every step has pacing, a deadline and
/// a cap; a retainer that will not open is skipped, never retried in a loop.
/// </summary>
public sealed class RetainerTrip
{
    public static readonly TimeSpan Pacing = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TeleportTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan HopTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan WalkTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan WalkGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MountWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DismountWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AddonTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan NumericTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan WithdrawSettle = TimeSpan.FromSeconds(3);

    public const float Reach = 3f;
    public const float ArrivalSlack = 1.5f;
    public const float MountDistance = 40f;
    public const int MaxAttempts = 3;

    private enum Step
    {
        Idle,
        Choosing,
        Teleporting,
        Hopping,
        Mounting,
        Walking,
        Dismounting,
        Summoning,
        Selecting,
        Menu,
        Bags,
        Withdrawing,
        Numeric,
        Settling,
        ClosingBags,
        Quitting,
        ClosingList,
    }

    private readonly IRetainerWorld _world;
    private readonly Func<uint, IReadOnlyList<ulong>> _holdersOf;
    private readonly Func<IReadOnlyList<ulong>> _neverIndexed;
    private readonly Func<ulong, string> _nameOf;
    private readonly Action<ulong, uint, int, int>? _withdrew;
    private readonly Dictionary<uint, FetchItem> _owed = new();
    private readonly Dictionary<uint, string> _notes = new();
    private readonly Queue<ulong> _visits = new();
    private readonly HashSet<ulong> _visited = new();
    private readonly List<uint> _items = new();

    private Step _step;
    private DateTime _stepStarted;
    private DateTime _issuedAt;
    private DateTime _nextActionAt;
    private int _attempts;
    private bool _sweep;
    private bool _planned;
    private Vector3 _destination;
    private TravelRoute? _route;
    private ulong _current;
    private int _itemIndex;
    private int _heldBefore;
    private int _pendingTake;
    private bool _pendingHq;

    /// <param name="holdersOf">Retainers the index says hold an item, most of it first.</param>
    /// <param name="neverIndexed">Retainers whose bags have never been seen.</param>
    /// <param name="withdrew">Told after each verified withdrawal (retainer, item, NQ, HQ), so the index stays honest.</param>
    public RetainerTrip(
        IRetainerWorld world, Func<uint, IReadOnlyList<ulong>> holdersOf, Func<IReadOnlyList<ulong>> neverIndexed,
        Func<ulong, string> nameOf, Action<ulong, uint, int, int>? withdrew = null)
    {
        _world = world;
        _holdersOf = holdersOf;
        _neverIndexed = neverIndexed;
        _nameOf = nameOf;
        _withdrew = withdrew;
    }

    public bool Active => _step != Step.Idle;

    /// <summary>Stopped on something only a person can fix (bags full).</summary>
    public bool NeedsPerson { get; private set; }

    public string Status { get; private set; } = "Idle — no retainer trip yet.";

    /// <summary>Why an item was not withdrawn, by item id.</summary>
    public IReadOnlyDictionary<uint, string> Notes => _notes;

    /// <param name="sweep">Also open every retainer never indexed, and take what is owed wherever it turns up.</param>
    public void Start(IReadOnlyList<FetchItem> items, bool sweep, DateTime now)
    {
        _owed.Clear();
        _notes.Clear();
        _visits.Clear();
        _visited.Clear();
        _items.Clear();
        NeedsPerson = false;
        _sweep = sweep;
        _planned = false;
        _current = 0;
        _route = null;

        foreach (var item in items)
            _owed[item.ItemId] = item;

        Enter(Step.Choosing, now);
        Status = sweep ? $"Visiting retainers for {items.Count} material(s), unindexed ones included" : $"Withdrawing {items.Count} material(s) from retainers";
    }

    public void Stop()
    {
        if (!Active)
            return;

        _world.StopWalking();
        if (_step is Step.Teleporting or Step.Hopping)
            _world.AbortTravel();
        if (_world.BagsOpen)
            _world.CloseBags();
        if (_world.RetainerListOpen)
            _world.CloseRetainerList();

        _step = Step.Idle;
        Status = "Stopped.";
    }

    public void Tick(DateTime now)
    {
        switch (_step)
        {
            case Step.Choosing: TickChoosing(now); break;
            case Step.Teleporting: TickTravel(now, hop: false); break;
            case Step.Hopping: TickTravel(now, hop: true); break;
            case Step.Mounting: TickMounting(now); break;
            case Step.Walking: TickWalking(now); break;
            case Step.Dismounting: TickDismounting(now); break;
            case Step.Summoning: TickSummoning(now); break;
            case Step.Selecting: TickSelecting(now); break;
            case Step.Menu: TickMenu(now); break;
            case Step.Bags: TickBags(now); break;
            case Step.Withdrawing: TickWithdrawing(now); break;
            case Step.Numeric: TickNumeric(now); break;
            case Step.Settling: TickSettling(now); break;
            case Step.ClosingBags: TickClosingBags(now); break;
            case Step.Quitting: TickQuitting(now); break;
            case Step.ClosingList: TickClosingList(now); break;
        }
    }

    // ---- choosing where to go and whom to see -----------------------------------------------------------------

    private void TickChoosing(DateTime now)
    {
        if (now < _nextActionAt)
            return;

        foreach (var done in _owed.Values.Where(i => _world.Held(i.ItemId) >= i.Target).ToList())
            _owed.Remove(done.ItemId);

        if (_owed.Count == 0)
        {
            Leave(now);
            return;
        }

        if (!_planned)
        {
            _planned = true;
            foreach (var retainer in PlanVisits())
                _visits.Enqueue(retainer);
        }

        if (_visits.Count == 0)
        {
            foreach (var item in _owed.Values)
                _notes.TryAdd(item.ItemId, _visited.Count == 0 ? "no retainer is known to hold it" : "the retainers visited did not have enough");
            _owed.Clear();
            Leave(now);
            return;
        }

        if (_world.RetainerListOpen)
        {
            Enter(Step.Selecting, now);
            return;
        }

        if (!_world.Ready || _world.AtABell)
        {
            if (now - _stepStarted > ReadyWait)
            {
                GiveUpAll(_world.AtABell ? "a summoning bell is already in use" : "the character stayed busy (occupied, in combat or crafting)", now);
                return;
            }

            Status = _world.AtABell ? "Waiting for the bell to be free" : "Waiting to be free to move";
            return;
        }

        if (_world.NearestBell() is { } bell)
        {
            _destination = bell;
            _route = null;
            Status = "Walking to a summoning bell";
            EnterApproach(now);
            return;
        }

        if (_world.RouteToABell() is { } route)
        {
            _route = route;
            Status = "Teleporting to a summoning bell";
            Enter(route.AetheryteId != 0 ? Step.Teleporting : Step.Hopping, now);
            return;
        }

        GiveUpAll("no summoning bell nearby, and no attuned city aetheryte with one to teleport to", now);
    }

    /// <summary>Whoever holds what is owed, the ones holding more of the list first; then, on a sweep, the never-indexed.</summary>
    private IEnumerable<ulong> PlanVisits()
    {
        var known = _world.Roster.Where(r => r.Id != 0).ToDictionary(r => r.Id, r => r);
        var coverage = new Dictionary<ulong, int>();
        foreach (var item in _owed.Keys)
        {
            foreach (var retainer in _holdersOf(item))
            {
                if (known.ContainsKey(retainer) && !_visited.Contains(retainer))
                    coverage[retainer] = coverage.GetValueOrDefault(retainer) + 1;
            }
        }

        var visits = coverage.OrderByDescending(c => c.Value).Select(c => c.Key).ToList();
        if (_sweep)
        {
            foreach (var retainer in _neverIndexed())
            {
                if (known.ContainsKey(retainer) && !_visited.Contains(retainer) && !visits.Contains(retainer))
                    visits.Add(retainer);
            }
        }

        return visits;
    }

    private void EnterApproach(DateTime now)
    {
        var far = Vector3.Distance(_world.PlayerPosition, _destination) > MountDistance;
        Enter(far && !_world.Mounted && _world.CanMount ? Step.Mounting : Step.Walking, now);
    }

    // ---- getting there ---------------------------------------------------------------------------------------

    private void TickTravel(DateTime now, bool hop)
    {
        var route = _route!.Value;
        var arrivesIn = hop ? route.Territory : route.ArrivesIn;

        if (_world.Territory == arrivesIn && _world.Ready && !_world.Travelling)
        {
            if (!hop && route.AethernetPlaceNameId != 0)
            {
                Enter(Step.Hopping, now);
                return;
            }

            _route = null;
            _nextActionAt = now + Pacing;
            Enter(Step.Choosing, now); // the bell is found live, now that we are there
            return;
        }

        var timeout = hop ? HopTimeout : TeleportTimeout;
        if (now - _stepStarted > timeout * MaxAttempts)
        {
            _world.AbortTravel();
            GiveUpAll(hop ? "the aethernet hop never arrived" : "the teleport never landed", now);
            return;
        }

        if (_world.Travelling || !_world.Ready || (_issuedAt != default && now - _issuedAt < timeout))
            return;

        if (_attempts >= MaxAttempts)
        {
            GiveUpAll(hop ? "the aethernet hop never arrived" : "the teleport never landed", now);
            return;
        }

        _attempts++;
        _issuedAt = now;
        if (hop)
            _world.AethernetHop(route.AethernetPlaceNameId);
        else
            _world.Teleport(route.AetheryteId);
    }

    private void TickMounting(DateTime now)
    {
        if (_world.Mounted || now - _stepStarted > MountWait * MaxAttempts || _attempts >= MaxAttempts || !_world.CanMount)
        {
            Enter(Step.Walking, now);
            return;
        }

        if (_issuedAt != default && now - _issuedAt < MountWait)
            return;

        _attempts++;
        _issuedAt = now;
        _world.Mount();
    }

    private void TickWalking(DateTime now)
    {
        var destination = _world.NearestBell() ?? _destination;
        if (Vector3.Distance(_world.PlayerPosition, destination) <= Reach + ArrivalSlack)
        {
            _world.StopWalking();
            Enter(_world.Mounted ? Step.Dismounting : Step.Summoning, now);
            return;
        }

        if (now - _stepStarted > WalkTimeout)
        {
            _world.StopWalking();
            GiveUpAll("could not walk to the bell in time", now);
            return;
        }

        if (!_world.CanWalk)
        {
            GiveUpAll("no navigation plugin (vnavmesh or Ariadne) is ready to walk there", now);
            return;
        }

        if (_world.Walking || (_issuedAt != default && now - _issuedAt < WalkGrace))
            return;

        if (_attempts >= MaxAttempts)
        {
            GiveUpAll("the path to the bell keeps stopping short", now);
            return;
        }

        _attempts++;
        _issuedAt = now;
        _world.WalkTo(destination, Reach);
    }

    private void TickDismounting(DateTime now)
    {
        if (!_world.Mounted || now - _stepStarted > DismountWait)
        {
            Enter(Step.Summoning, now);
            return;
        }

        if (_issuedAt == default || now - _issuedAt >= Pacing)
        {
            _issuedAt = now;
            _world.Dismount();
        }
    }

    // ---- the bell ----------------------------------------------------------------------------------------------

    private void TickSummoning(DateTime now)
    {
        if (_world.RetainerListOpen)
        {
            Enter(Step.Selecting, now);
            return;
        }

        if (_world.AdvanceTalk())
            return;

        if (now - _stepStarted > AddonTimeout * MaxAttempts)
        {
            GiveUpAll("the summoning bell would not open", now);
            return;
        }

        if (now >= _nextActionAt)
        {
            _nextActionAt = now + Pacing * 2;
            _world.InteractWithBell();
        }
    }

    private void TickSelecting(DateTime now)
    {
        if (!_world.RetainerListOpen)
        {
            if (now - _stepStarted > AddonTimeout)
                Enter(Step.Summoning, now);
            return;
        }

        while (_visits.Count > 0 && _visited.Contains(_visits.Peek()))
            _visits.Dequeue();

        if (_visits.Count == 0)
        {
            Enter(Step.ClosingList, now);
            return;
        }

        if (now < _nextActionAt)
            return;

        _current = _visits.Dequeue();
        _visited.Add(_current);
        Status = $"Opening {_nameOf(_current)}";
        if (_world.SelectRetainer(_current))
        {
            Enter(Step.Menu, now);
            return;
        }

        _nextActionAt = now + Pacing;
    }

    private void TickMenu(DateTime now)
    {
        if (_world.AdvanceTalk())
            return;

        if (_world.BagsOpen)
        {
            Enter(Step.Bags, now);
            return;
        }

        if (now - _stepStarted > AddonTimeout)
        {
            SkipRetainer("their menu never opened", now);
            return;
        }

        if (_world.MenuOpen && now >= _nextActionAt)
        {
            _nextActionAt = now + Pacing;
            _world.ChooseMenuEntry("withdraw", fallbackIndex: 0);
        }
    }

    private void TickBags(DateTime now)
    {
        if (_world.AdvanceTalk())
            return;

        if (_world.BagsOpen)
        {
            _items.Clear();
            foreach (var item in _owed.Values)
            {
                if (_world.InRetainerBags(item.ItemId).Total > 0)
                    _items.Add(item.ItemId);
            }

            _itemIndex = 0;
            Enter(Step.Withdrawing, now);
            return;
        }

        if (now - _stepStarted > AddonTimeout)
            SkipRetainer("their bags never opened", now);
    }

    private void TickWithdrawing(DateTime now)
    {
        if (!_world.BagsOpen)
        {
            SkipRetainer("their bags closed", now);
            return;
        }

        if (_itemIndex >= _items.Count)
        {
            Enter(Step.ClosingBags, now);
            return;
        }

        var itemId = _items[_itemIndex];
        if (!_owed.TryGetValue(itemId, out var item) || _world.Held(itemId) >= item.Target)
        {
            _itemIndex++;
            return;
        }

        var have = _world.InRetainerBags(itemId);
        if (have.Total == 0)
        {
            _itemIndex++;
            return;
        }

        var owed = item.Target - _world.Held(itemId);
        _pendingHq = have.Hq > 0; // HQ first: it is the better material and the rarer stack
        var take = Math.Min(owed, _pendingHq ? have.Hq : have.Nq);
        _heldBefore = _world.Held(itemId);
        _pendingTake = _world.Retrieve(itemId, _pendingHq, take, out var asksQuantity);
        if (_pendingTake <= 0)
        {
            _itemIndex++;
            return;
        }

        Status = $"Withdrawing {_pendingTake} × {item.Name} from {_nameOf(_current)}";
        Enter(asksQuantity ? Step.Numeric : Step.Settling, now);
    }

    private void TickNumeric(DateTime now)
    {
        if (_world.NumericOpen)
        {
            if (now >= _nextActionAt)
            {
                _nextActionAt = now + Pacing;
                _world.EnterQuantity(_pendingTake);
            }

            return;
        }

        if (_issuedAt == default)
            _issuedAt = now; // it has not appeared yet: give it a moment
        if (now - _stepStarted > NumericTimeout)
            Enter(Step.Settling, now); // no prompt: judge by the bags either way
    }

    private void TickSettling(DateTime now)
    {
        if (_world.NumericOpen)
            return;

        var itemId = _items[_itemIndex];
        var landed = _world.Held(itemId) - _heldBefore;
        if (landed >= _pendingTake)
        {
            _withdrew?.Invoke(_current, itemId, _pendingHq ? 0 : landed, _pendingHq ? landed : 0);
            Enter(Step.Withdrawing, now); // the same item again until it is covered, then the next
            return;
        }

        if (now - _stepStarted < WithdrawSettle)
            return;

        if (landed <= 0 && _world.FreeBagSlots == 0)
        {
            NeedsPerson = true;
            foreach (var owed in _owed.Values)
                _notes[owed.ItemId] = owed.ItemId == itemId ? "your bags are full" : "not withdrawn: your bags are full";
            _owed.Clear();
            Leave(now);
            return;
        }

        _notes[itemId] = landed <= 0 ? "the withdrawal did not land" : $"only {landed} of {_pendingTake} arrived";
        _itemIndex++;
        Enter(Step.Withdrawing, now);
    }

    private void TickClosingBags(DateTime now)
    {
        if (!_world.BagsOpen)
        {
            Enter(Step.Quitting, now);
            return;
        }

        if (now - _stepStarted > AddonTimeout)
        {
            Enter(Step.Quitting, now);
            return;
        }

        if (now >= _nextActionAt)
        {
            _nextActionAt = now + Pacing;
            _world.CloseBags();
        }
    }

    private void TickQuitting(DateTime now)
    {
        if (_world.AdvanceTalk())
            return;

        if (_world.MenuOpen)
        {
            if (now >= _nextActionAt)
            {
                _nextActionAt = now + Pacing;
                _world.ChooseMenuEntry("quit", fallbackIndex: -1);
            }

            if (now - _stepStarted > AddonTimeout)
                Enter(Step.ClosingList, now);
            return;
        }

        if (_world.RetainerListOpen)
        {
            _nextActionAt = now + Pacing;
            Enter(Step.Choosing, now); // more retainers, or done
            return;
        }

        if (now - _stepStarted > AddonTimeout)
            Enter(Step.Choosing, now);
    }

    private void TickClosingList(DateTime now)
    {
        if (!_world.RetainerListOpen)
        {
            _nextActionAt = now + Pacing;
            Enter(Step.Choosing, now);
            return;
        }

        if (_world.AdvanceTalk())
            return;

        if (now - _stepStarted > AddonTimeout)
        {
            Enter(Step.Choosing, now);
            return;
        }

        if (now >= _nextActionAt)
        {
            _nextActionAt = now + Pacing;
            _world.CloseRetainerList();
        }
    }

    // ---- bookkeeping ----------------------------------------------------------------------------------------------

    private void SkipRetainer(string reason, DateTime now)
    {
        Status = $"Skipping {_nameOf(_current)}: {reason}";
        if (_world.BagsOpen)
        {
            Enter(Step.ClosingBags, now);
            return;
        }

        Enter(_world.MenuOpen ? Step.Quitting : Step.Selecting, now);
    }

    /// <summary>Nothing more can be done: close whatever is open, then finish.</summary>
    private void Leave(DateTime now)
    {
        if (_world.BagsOpen)
        {
            Enter(Step.ClosingBags, now);
            return;
        }

        if (_world.MenuOpen)
        {
            Enter(Step.Quitting, now);
            return;
        }

        if (_world.RetainerListOpen)
        {
            _visits.Clear();
            Enter(Step.ClosingList, now);
            return;
        }

        Finish();
    }

    private void GiveUpAll(string reason, DateTime now)
    {
        foreach (var item in _owed.Values)
            _notes.TryAdd(item.ItemId, reason);
        _owed.Clear();
        _world.StopWalking();
        Leave(now);
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
            ? "Stopped withdrawing: your bags are full."
            : _notes.Count == 0
                ? "Withdrew everything."
                : $"Withdrew what could be withdrawn; {_notes.Count} item(s) not.";
    }
}
