using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Hephaestus.Features.Materials;

/// <summary>One occupied slot on a Free Company chest page.</summary>
public readonly record struct ChestStack(int Page, int Slot, uint ItemId, int Quantity, bool Hq);

/// <summary>
/// Everything a chest trip reads from and does to the game — the one seam, so the trip is pure and
/// tested (Odysseus's IChestWorld, widened with getting there). The adapter is Services/Game/ChestWorld.
/// </summary>
public interface IChestWorld
{
    bool Ready { get; }

    bool Travelling { get; }

    Vector3 PlayerPosition { get; }

    int Held(uint itemId);

    int FreeBagSlots { get; }

    /// <summary>The Company Chest object spawned around the character (EObj 2000470), if any.</summary>
    Vector3? NearestChest();

    /// <summary>Lifestream is loaded: the FC estate is one command away.</summary>
    bool CanGoToEstate { get; }

    /// <summary>Teleport to the free company estate and walk in (Lifestream's <c>/li fc</c>).</summary>
    bool GoToEstate();

    void AbortTravel();

    bool CanWalk { get; }

    bool Walking { get; }

    bool WalkTo(Vector3 destination, float within);

    void StopWalking();

    bool Mounted { get; }

    bool CanMount { get; }

    void Mount();

    void Dismount();

    bool InteractWithChest();

    bool AdvanceTalk();

    /// <summary>The chest window is open. That window IS the transfer session.</summary>
    bool ChestOpen { get; }

    /// <summary>A page's container has arrived — its tab was viewed this session. Pages are 1..5.</summary>
    bool PageLoaded(int page);

    /// <summary>Click the page's tab, so its container loads.</summary>
    bool ViewPage(int page);

    /// <summary>Stacks of an item on the loaded pages.</summary>
    IReadOnlyList<ChestStack> ChestStacks(uint itemId);

    /// <summary>Move a whole stack into the bags. False when there was nowhere to put it.</summary>
    bool Withdraw(ChestStack stack);

    /// <summary>
    /// Start moving part of a stack — the game's click-the-stack, click-a-bag-slot move, which asks how
    /// many in its number prompt. False when there was nowhere to put it.
    /// </summary>
    bool WithdrawSome(ChestStack stack, int amount);

    /// <summary>The "how many?" prompt (InputNumeric) is up.</summary>
    bool QuantityPromptOpen { get; }

    /// <summary>Answer the prompt: OK with this many.</summary>
    void AnswerQuantity(int amount);

    /// <summary>How many of the stack's item are still in its slot — the only success signal worth trusting.</summary>
    int QuantityAt(ChestStack stack);

    void CloseChest();
}

/// <summary>
/// Takes materials out of the Free Company chest — the source that comes before the retainers.
///
/// Getting there: the Company Chest spawned nearby, else Lifestream to the estate and then the chest
/// found live; walk (mounted when far), dismount, interact. Reading it: a page is readable only once
/// its tab has been viewed this session, so every tab is clicked first (Charon's tab walk); the
/// stores — Charon's and ours — learn the pages while the window is open. Taking: exactly what is
/// short — a stack that covers the shortfall is split the way the game splits it by hand (the
/// click-the-stack move, answered through its "how many?" prompt), a stack that does not is taken
/// whole — each move verified by the source slot shrinking, never by the return code, which Charon
/// proved comes back 6 on moves that worked.
/// Bags full = a person-problem. Every step has pacing, a deadline and a cap.
/// </summary>
public sealed class ChestTrip
{
    public const int PageCount = 5;

    public static readonly TimeSpan Pacing = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan EstateTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan WalkTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan WalkGrace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MountWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DismountWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TabSettle = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MoveGap = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    public const float Reach = 3f;
    public const float ArrivalSlack = 1.5f;
    public const float MountDistance = 40f;
    public const int MaxAttempts = 3;

    private enum Step
    {
        Idle,
        Choosing,
        Travelling,
        Mounting,
        Walking,
        Dismounting,
        Opening,
        Viewing,
        Withdrawing,
        Settling,
        Closing,
    }

    private readonly IChestWorld _world;
    private readonly Dictionary<uint, FetchItem> _owed = new();
    private readonly Dictionary<uint, string> _notes = new();
    private readonly List<uint> _items = new();

    private Step _step;
    private DateTime _stepStarted;
    private DateTime _issuedAt;
    private DateTime _nextActionAt;
    private int _attempts;
    private bool _travelled;
    private Vector3 _destination;
    private int _page;
    private int _itemIndex;
    private ChestStack _inFlight;
    private int _inFlightAmount;
    private bool _promptAnswered;

    public ChestTrip(IChestWorld world)
    {
        _world = world;
    }

    public bool Active => _step != Step.Idle;

    /// <summary>Stopped on something only a person can fix (bags full).</summary>
    public bool NeedsPerson { get; private set; }

    public string Status { get; private set; } = "Idle — no chest trip yet.";

    /// <summary>Why an item was not taken, by item id.</summary>
    public IReadOnlyDictionary<uint, string> Notes => _notes;

    /// <summary>Whole stacks moved on the last trip.</summary>
    public int StacksMoved { get; private set; }

    /// <param name="sweep">Go and look even when the store says nothing: the pages get learned, and what is owed is taken if found.</param>
    public void Start(IReadOnlyList<FetchItem> items, bool sweep, DateTime now)
    {
        _owed.Clear();
        _notes.Clear();
        _items.Clear();
        NeedsPerson = false;
        StacksMoved = 0;
        _travelled = false;

        foreach (var item in items)
            _owed[item.ItemId] = item;

        Enter(Step.Choosing, now);
        Status = sweep ? $"Looking in the company chest for {items.Count} material(s)" : $"Taking {items.Count} material(s) from the company chest";
    }

    public void Stop()
    {
        if (!Active)
            return;

        _world.StopWalking();
        if (_step == Step.Travelling)
            _world.AbortTravel();
        if (_world.ChestOpen)
            _world.CloseChest();

        _step = Step.Idle;
        Status = "Stopped.";
    }

    public void Tick(DateTime now)
    {
        switch (_step)
        {
            case Step.Choosing: TickChoosing(now); break;
            case Step.Travelling: TickTravelling(now); break;
            case Step.Mounting: TickMounting(now); break;
            case Step.Walking: TickWalking(now); break;
            case Step.Dismounting: TickDismounting(now); break;
            case Step.Opening: TickOpening(now); break;
            case Step.Viewing: TickViewing(now); break;
            case Step.Withdrawing: TickWithdrawing(now); break;
            case Step.Settling: TickSettling(now); break;
            case Step.Closing: TickClosing(now); break;
        }
    }

    // ---- getting to the chest ---------------------------------------------------------------------------------

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

        if (_world.ChestOpen)
        {
            _page = 1;
            Enter(Step.Viewing, now);
            return;
        }

        if (!_world.Ready)
        {
            if (now - _stepStarted > ReadyWait)
            {
                GiveUpAll("the character stayed busy (occupied, in combat or crafting)", now);
                return;
            }

            Status = "Waiting to be free to move";
            return;
        }

        if (_world.NearestChest() is { } chest)
        {
            _destination = chest;
            Status = "Walking to the company chest";
            EnterApproach(now);
            return;
        }

        if (!_travelled && _world.CanGoToEstate)
        {
            _travelled = true;
            Status = "Going to the free company estate";
            _world.GoToEstate();
            Enter(Step.Travelling, now);
            return;
        }

        GiveUpAll(_travelled ? "no company chest could be found at the estate" : "no company chest nearby, and Lifestream is not loaded to reach the estate", now);
    }

    private void TickTravelling(DateTime now)
    {
        if (now - _stepStarted < WalkGrace)
            return; // Lifestream needs a moment to become busy

        if (_world.Travelling)
        {
            if (now - _stepStarted > EstateTimeout)
            {
                _world.AbortTravel();
                GiveUpAll("the trip to the estate never arrived", now);
            }

            return;
        }

        if (!_world.Ready)
            return;

        _nextActionAt = now + Pacing;
        Enter(Step.Choosing, now); // the chest is found live, now that we are there
    }

    private void EnterApproach(DateTime now)
    {
        var far = Vector3.Distance(_world.PlayerPosition, _destination) > MountDistance;
        Enter(far && !_world.Mounted && _world.CanMount ? Step.Mounting : Step.Walking, now);
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
        var destination = _world.NearestChest() ?? _destination;
        if (Vector3.Distance(_world.PlayerPosition, destination) <= Reach + ArrivalSlack)
        {
            _world.StopWalking();
            Enter(_world.Mounted ? Step.Dismounting : Step.Opening, now);
            return;
        }

        if (now - _stepStarted > WalkTimeout)
        {
            _world.StopWalking();
            GiveUpAll("could not walk to the chest in time", now);
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
            GiveUpAll("the path to the chest keeps stopping short", now);
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
            Enter(Step.Opening, now);
            return;
        }

        if (_issuedAt == default || now - _issuedAt >= Pacing)
        {
            _issuedAt = now;
            _world.Dismount();
        }
    }

    // ---- the chest -------------------------------------------------------------------------------------------------

    private void TickOpening(DateTime now)
    {
        if (_world.ChestOpen)
        {
            _page = 1;
            Enter(Step.Viewing, now);
            return;
        }

        if (_world.AdvanceTalk())
            return;

        if (now - _stepStarted > OpenTimeout * MaxAttempts)
        {
            GiveUpAll("the company chest would not open", now);
            return;
        }

        if (now >= _nextActionAt)
        {
            _nextActionAt = now + Pacing * 2;
            _world.InteractWithChest();
        }
    }

    /// <summary>Click every unviewed tab, so all five pages are readable; a page that never loads is skipped.</summary>
    private void TickViewing(DateTime now)
    {
        if (!_world.ChestOpen)
        {
            GiveUpAll("the company chest closed", now);
            return;
        }

        if (_issuedAt != default)
        {
            if (now - _issuedAt < TabSettle)
                return; // let the tab land before anything else is clicked (Charon's TabSettle)
            if (!_world.PageLoaded(_page) && now - _issuedAt < PageTimeout)
                return; // the container is still on its way

            _page++; // loaded — or unreadable this time, in which case what is on it stays unknown, never "absent"
            _issuedAt = default;
        }

        while (_page <= PageCount && _world.PageLoaded(_page))
            _page++;

        if (_page > PageCount)
        {
            _items.Clear();
            _items.AddRange(_owed.Keys);
            _itemIndex = 0;
            Enter(Step.Withdrawing, now);
            return;
        }

        _issuedAt = now;
        Status = $"Viewing chest page {_page}";
        _world.ViewPage(_page);
    }

    private void TickWithdrawing(DateTime now)
    {
        if (!_world.ChestOpen)
        {
            GiveUpAll("the company chest closed", now);
            return;
        }

        if (now - _issuedAt < MoveGap)
            return;

        if (_itemIndex >= _items.Count)
        {
            Enter(Step.Closing, now);
            return;
        }

        var itemId = _items[_itemIndex];
        if (!_owed.TryGetValue(itemId, out var item) || _world.Held(itemId) >= item.Target)
        {
            _itemIndex++;
            return;
        }

        var stacks = _world.ChestStacks(itemId);
        if (stacks.Count == 0)
        {
            // Not on any loaded page. Not the same as "not in the chest": an unviewed page is unreadable.
            _notes[itemId] = "none on the chest pages that could be read";
            _itemIndex++;
            return;
        }

        // Smallest stack that still covers the shortfall, else the largest. A covering stack is split
        // down to exactly the shortfall; a smaller one is taken whole and the trip comes back for the rest.
        var missing = item.Target - _world.Held(itemId);
        ChestStack? pick = null;
        foreach (var stack in stacks)
        {
            if (stack.Quantity >= missing)
            {
                if (pick is null || pick.Value.Quantity < missing || stack.Quantity < pick.Value.Quantity)
                    pick = stack;
            }
            else if (pick is null || (pick.Value.Quantity < missing && stack.Quantity > pick.Value.Quantity))
            {
                pick = stack;
            }
        }

        var amount = pick is null ? 0 : Math.Min(pick.Value.Quantity, missing);
        if (pick is not { } chosen || !(amount < chosen.Quantity ? _world.WithdrawSome(chosen, amount) : _world.Withdraw(chosen)))
        {
            if (_world.FreeBagSlots == 0)
            {
                NeedsPerson = true;
                foreach (var owed in _owed.Values)
                    _notes[owed.ItemId] = owed.ItemId == itemId ? "your bags are full" : "not taken: your bags are full";
                _owed.Clear();
                Leave(now);
                return;
            }

            _notes[itemId] = "the game would not take the stack (no room that fits it?)";
            _itemIndex++;
            return;
        }

        _inFlight = chosen;
        _inFlightAmount = amount;
        _promptAnswered = false;
        _issuedAt = now;
        Status = amount < chosen.Quantity
            ? $"Taking {amount} of {chosen.Quantity} × {item.Name} from chest page {chosen.Page}"
            : $"Taking {chosen.Quantity} × {item.Name} from chest page {chosen.Page}";
        Enter(Step.Settling, now);
    }

    private void TickSettling(DateTime now)
    {
        // A split waits on the game's "how many?" prompt before anything moves.
        if (_inFlightAmount < _inFlight.Quantity && !_promptAnswered)
        {
            if (_world.QuantityPromptOpen)
            {
                _world.AnswerQuantity(_inFlightAmount);
                _promptAnswered = true;
                _stepStarted = now; // the move only starts now; give it the full window
                return;
            }

            if (now - _stepStarted <= MoveTimeout)
                return;

            _notes[_inFlight.ItemId] = "the chest never asked how many — nothing moved";
            _itemIndex++;
            Enter(Step.Withdrawing, now);
            return;
        }

        if (_world.QuantityAt(_inFlight) <= _inFlight.Quantity - _inFlightAmount)
        {
            StacksMoved++;
            _issuedAt = now;
            Enter(Step.Withdrawing, now); // the same item again until it is covered, then the next
            return;
        }

        if (now - _stepStarted <= MoveTimeout)
            return; // a chest move is a server round trip

        _notes[_inFlight.ItemId] = "a stack did not leave the chest";
        _itemIndex++;
        Enter(Step.Withdrawing, now);
    }

    private void TickClosing(DateTime now)
    {
        if (!_world.ChestOpen || now - _stepStarted > CloseTimeout)
        {
            Finish();
            return;
        }

        if (now >= _nextActionAt)
        {
            _nextActionAt = now + Pacing;
            _world.CloseChest();
        }
    }

    // ---- bookkeeping --------------------------------------------------------------------------------------------------

    private void Leave(DateTime now)
    {
        if (_world.ChestOpen)
        {
            Enter(Step.Closing, now);
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
        if (step != Step.Withdrawing)
            _issuedAt = default;
        _attempts = 0;
    }

    private void Finish()
    {
        _step = Step.Idle;
        Status = NeedsPerson
            ? "Stopped at the chest: your bags are full."
            : _notes.Count == 0
                ? $"Took everything ({StacksMoved} stack(s))."
                : $"Took what could be taken ({StacksMoved} stack(s)); {_notes.Count} item(s) not.";
    }
}
