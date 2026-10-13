using System;
using System.Collections.Generic;
using System.Linq;
using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;
using Hephaestus.Services.Game;

namespace Hephaestus.Services;

/// <summary>
/// Buying and gathering (user, 2026-09-21): the Buy List and Gather List tabs' back end — lists made
/// on request from the queue, bag counts kept current from inventory events, and the buttons each tab
/// ends with (Auto purchase runs a <see cref="VendorTrip"/> for what is still missing; Auto gather asks
/// Odysseus to gather it now; Send to Odysseus hands the list over without starting it) — and the
/// queue's own fetches when this character has automatic sourcing on (free trials by default; a paid
/// account may buy some things on the market board first and press the buttons). The same trip and
/// the same Odysseus request serve both. NPC vendors only: the market board is never automated.
///
/// Nothing here scans the bags on a timer: <see cref="OnInventoryChanged"/> marks them stale and the
/// next tick re-reads just the listed items.
/// </summary>
public sealed class SupplyDesk
{
    private readonly MaterialPlanner _planner;
    private readonly CharacterQueue _queue;
    private readonly Func<uint> _currentJob;
    private readonly VendorCatalog _vendors;
    private readonly Func<long> _gil;
    private readonly OdysseusGatherClient _odysseus;
    private readonly VendorTrip _trip;
    private readonly RetainerTrip _retainerTrip;
    private readonly CharacterRetainers _retainers;
    private readonly CharonRetainerClient _charon;
    private readonly ChestTrip _chestTrip;
    private readonly CharonChestClient _charonChest;
    private readonly Func<uint, int, int> _chestHeldOnPage;
    private readonly Func<int, bool> _chestPageLoaded;
    private readonly Func<IReadOnlyList<RetainerSlot>> _roster;
    private bool _retainerTripWasActive;
    private bool _chestTripWasActive;
    private readonly Func<bool> _queueBusy;
    private readonly Func<bool> _logOpen;
    private readonly Action _closeLog;
    private readonly Func<uint, string> _recipeName;
    private readonly Func<bool> _freeTrial;
    private bool _bagsStale;
    private FetchKind? _queueFetch;

    /// <param name="queueBusy">The queue or a craft run has the character: nothing here may start.</param>
    public SupplyDesk(
        MaterialPlanner planner, CharacterQueue queue, Func<uint> currentJob, VendorCatalog vendors, Func<long> gil,
        OdysseusGatherClient odysseus, VendorTrip trip, RetainerTrip retainerTrip, CharacterRetainers retainers, CharonRetainerClient charon,
        Func<IReadOnlyList<RetainerSlot>> roster, ChestTrip chestTrip, CharonChestClient charonChest, Func<uint, int, int> chestHeldOnPage, Func<int, bool> chestPageLoaded,
        Func<bool> queueBusy, Func<bool> logOpen, Action closeLog, Func<uint, string> recipeName, Func<bool> freeTrial)
    {
        _planner = planner;
        _queue = queue;
        _currentJob = currentJob;
        _vendors = vendors;
        _gil = gil;
        _odysseus = odysseus;
        _trip = trip;
        _retainerTrip = retainerTrip;
        _retainers = retainers;
        _charon = charon;
        _roster = roster;
        _chestTrip = chestTrip;
        _charonChest = charonChest;
        _chestHeldOnPage = chestHeldOnPage;
        _chestPageLoaded = chestPageLoaded;
        _queueBusy = queueBusy;
        _logOpen = logOpen;
        _closeLog = closeLog;
        _recipeName = recipeName;
        _freeTrial = freeTrial;
    }

    /// <summary>No market board on a free trial: what vendors and gathering do not cover cannot be had at all.</summary>
    public bool FreeTrial => _freeTrial();

    public SupplyList? BuyList { get; private set; }

    public SupplyList? GatherList { get; private set; }

    /// <summary>Bumped on every inventory change; windows compare it instead of re-reading anything.</summary>
    public long InventoryVersion { get; private set; }

    /// <summary>Gil held, as of the last inventory change — read on events, not every frame.</summary>
    public long Gil { get; private set; }

    /// <summary>A vendor trip is driving the character.</summary>
    public bool Buying => _trip.Active;

    /// <summary>
    /// Retainers and the FC chest (user, 2026-09-26): Hephaestus DRIVES the bell and the chest with its own
    /// trips; Charon, the fleet's retainer plugin, is the STORE when it is loaded (its reader captures whatever
    /// retainer or chest window is open, ours included), and our own index or the live pages are the store
    /// when it is not.
    /// </summary>
    public bool ViaCharon => _charon.Available;

    /// <summary>Our retainer trip is driving the character.</summary>
    public bool Withdrawing => _retainerTrip.Active;

    public string WithdrawStatus => _retainerTrip.Status;

    /// <summary>Our chest trip is driving the character.</summary>
    public bool FetchingFromChest => _chestTrip.Active;

    public string ChestStatus => _chestTrip.Status;

    /// <summary>Retainers as a source: a paid account only (a free trial has none); Charon's store first, else our own index.</summary>
    public RetainerSupply? RetainerSupply()
    {
        if (FreeTrial || _retainers.ContentId == 0)
            return null;
        if (ViaCharon)
            return new RetainerSupply(_charon.Held, () => UnknownRetainerIds().Count);
        return new RetainerSupply(_retainers.Index.Held, () => _retainers.Index.NeverIndexed().Count);
    }

    /// <summary>
    /// The chest as a source: a paid account only (no FC on a free trial). Page by page: a page loaded in this
    /// client is read LIVE (it is current the moment our trip moves a stack; Charon re-captures an open chest
    /// only every 20 s, so its snapshot can still show what we just took), and a page not loaded here comes
    /// from Charon's store, as last seen. Nothing loaded and Charon has never seen the chest = unknown.
    /// </summary>
    public ChestSupply? ChestSupply()
    {
        if (FreeTrial || _retainers.ContentId == 0)
            return null;

        return new ChestSupply(
            itemId =>
            {
                var charon = _charonChest.Available;
                var total = 0;
                for (var page = 1; page <= ChestTrip.PageCount; page++)
                    total += _chestPageLoaded(page) ? _chestHeldOnPage(itemId, page) : charon ? _charonChest.HeldOnPage(itemId, page) : 0;
                return total;
            },
            () => AnyChestPageLoaded() || (_charonChest.Available && _charonChest.Known()));
    }

    private bool AnyChestPageLoaded()
    {
        for (var page = 1; page <= ChestTrip.PageCount; page++)
        {
            if (_chestPageLoaded(page))
                return true;
        }

        return false;
    }

    /// <summary>Who holds an item, as retainer ids for the trip: Charon's names mapped through the live roster, else our index.</summary>
    public IReadOnlyList<ulong> HoldersOf(uint itemId)
    {
        if (!ViaCharon)
            return _retainers.Index.Holders(itemId).Select(h => h.Retainer.RetainerId).ToList();

        var byName = RosterByName();
        var ids = new List<ulong>();
        foreach (var holder in _charon.HoldersOf(itemId))
        {
            if (byName.TryGetValue(holder.Retainer, out var id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>Retainers whose bags no store has seen, as ids for a sweep.</summary>
    public IReadOnlyList<ulong> UnknownRetainerIds()
    {
        if (!ViaCharon)
            return _retainers.Index.NeverIndexed().Select(r => r.RetainerId).ToList();

        var byName = RosterByName();
        var ids = new List<ulong>();
        foreach (var name in _charon.UnknownRetainers(0))
        {
            if (byName.TryGetValue(name, out var id))
                ids.Add(id);
        }

        return ids;
    }

    private Dictionary<string, ulong> RosterByName()
    {
        var byName = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in _roster())
        {
            if (slot.Id != 0 && slot.Name.Length > 0)
                byName[slot.Name] = slot.Id;
        }

        return byName;
    }

    /// <summary>For the Debug tab: where chest answers come from, and what the chest trip is doing.</summary>
    public string ChestSourceStatus
        => FreeTrial
            ? "not a source on a free trial (no free company)"
            : (_charonChest.Available ? $"store: pages loaded here, else Charon — {_charonChest.Status}" : "store: the pages loaded this session (Charon not loaded)") + $"; trip: {_chestTrip.Status}";

    /// <summary>For the Debug tab: where retainer answers come from right now.</summary>
    public string RetainerSourceStatus(string ownIndexStatus)
        => ViaCharon ? $"store: Charon — {_charon.Status}; trips: ours" : $"store: own index (Charon not loaded) — {ownIndexStatus}; trips: ours";

    public string BuyStatus => _trip.Status;

    public IReadOnlyDictionary<uint, string> BuyNotes => _trip.Notes;

    /// <summary>Odysseus is gathering our list right now.</summary>
    public bool Gathering => _odysseus.Active;

    public string GatherStatus => _odysseus.Available ? _odysseus.Status : "Odysseus is not loaded, or too old to gather on request.";

    public IReadOnlyDictionary<uint, string> GatherNotes => _odysseus.Notes;

    public bool OdysseusAvailable => _odysseus.Available;

    /// <summary>Odysseus takes a list without starting it (its <c>Gather.SetList</c> gate exists).</summary>
    public bool CanHandOver => _odysseus.CanHandOver;

    /// <summary>Something here or the queue has the character: the buttons that move it are off.</summary>
    public bool Busy => _queueBusy() || Buying || Gathering || Withdrawing || FetchingFromChest;

    public void OnInventoryChanged()
    {
        _bagsStale = true;
        InventoryVersion++;
    }

    /// <summary>What the queue may fetch on its own: vendors always, gathering when Odysseus is there.</summary>
    public MaterialSources Sources()
        => new(new VendorSupply(_vendors.PriceOf, _gil()), _odysseus.Available ? new GatherSupply(_odysseus.CanGather) : null, RetainerSupply(), ChestSupply());

    /// <summary>The queue's own fetch: the trip or the Odysseus request the buttons use. The queue has closed the log already.</summary>
    public void StartForQueue(FetchRequest request)
    {
        _queueFetch = request.Kind;
        switch (request.Kind)
        {
            case FetchKind.ChestWithdraw:
                _chestTrip.Start(request.Items, request.Sweep, DateTime.UtcNow);
                break;
            case FetchKind.Withdraw:
                _retainerTrip.Start(request.Items, request.Sweep, DateTime.UtcNow);
                break;
            case FetchKind.Buy:
                _trip.Start(request.Items, DateTime.UtcNow);
                break;
            default:
                _odysseus.Start(request.Items);
                break;
        }
    }

    public bool QueueFetchActive
        => _queueFetch switch { FetchKind.ChestWithdraw => FetchingFromChest, FetchKind.Withdraw => Withdrawing, FetchKind.Buy => _trip.Active, FetchKind.Gather => _odysseus.Active, _ => false };

    public bool QueueFetchNeedsPerson
        => _queueFetch switch { FetchKind.ChestWithdraw => _chestTrip.NeedsPerson, FetchKind.Withdraw => _retainerTrip.NeedsPerson, FetchKind.Buy => _trip.NeedsPerson, FetchKind.Gather => _odysseus.NeedsPerson, _ => false };

    public string QueueFetchStatus
        => _queueFetch switch { FetchKind.ChestWithdraw => ChestStatus, FetchKind.Withdraw => WithdrawStatus, FetchKind.Buy => _trip.Status, FetchKind.Gather => _odysseus.Status, _ => string.Empty };

    public IReadOnlyDictionary<uint, string>? QueueFetchNotes
        => _queueFetch switch { FetchKind.ChestWithdraw => _chestTrip.Notes, FetchKind.Withdraw => _retainerTrip.Notes, FetchKind.Buy => _trip.Notes, FetchKind.Gather => _odysseus.Notes, _ => null };

    /// <summary>The Queue tab's button: withdraw what the plan says the retainers hold, now.</summary>
    public bool CanWithdraw(IReadOnlyList<PlannedWithdrawal> withdrawals) => withdrawals.Count > 0 && !Busy && RetainerSupply() != null;

    public void WithdrawNow(IReadOnlyList<PlannedWithdrawal> withdrawals)
    {
        if (!CanWithdraw(withdrawals))
            return;

        if (_logOpen())
            _closeLog();
        var items = withdrawals.Select(w => new FetchItem(w.ItemId, w.Name, _planner.Held(w.ItemId) + w.Quantity, w.Quantity)).ToList();
        _retainerTrip.Start(items, sweep: false, DateTime.UtcNow);
    }

    /// <summary>The Queue tab's button: take what the plan says is in the company chest, now.</summary>
    public bool CanFetchFromChest(IReadOnlyList<PlannedChestWithdrawal> withdrawals) => withdrawals.Count > 0 && !Busy && ChestSupply() != null;

    public void FetchFromChestNow(IReadOnlyList<PlannedChestWithdrawal> withdrawals)
    {
        if (!CanFetchFromChest(withdrawals))
            return;

        if (_logOpen())
            _closeLog();
        _chestTrip.Start(
            withdrawals.Select(w => new FetchItem(w.ItemId, w.Name, _planner.Held(w.ItemId) + w.Quantity, w.Quantity)).ToList(),
            sweep: false, DateTime.UtcNow);
    }

    /// <summary>(Re)make a list from the queue as it stands. Both come from one plan; only the one asked for is replaced.</summary>
    public void Make(SupplyKind kind)
    {
        var sources = Sources();
        var (buy, gather) = SupplyList.Make(
            _planner, _queue.List.Items, _currentJob(), sources.Vendors, sources.Gathering, _recipeName, DateTime.UtcNow);

        if (kind == SupplyKind.Buy)
            BuyList = buy;
        else
            GatherList = gather;
        Gil = _gil();
    }

    public void Clear(SupplyKind kind)
    {
        if (kind == SupplyKind.Buy)
            BuyList = null;
        else
            GatherList = null;
    }

    /// <summary>The character changed: another character's lists mean nothing here.</summary>
    public void Forget()
    {
        StopAll();
        BuyList = null;
        GatherList = null;
    }

    public IEnumerable<string> SellersOf(uint itemId)
        => _vendors.VendorsFor(itemId).Select(v => v.Name).Where(n => n.Length > 0).Distinct();

    public bool CanAutoPurchase => BuyList is { AllDone: false } && !Busy;

    /// <summary>Buy everything still missing: out of the crafting log, then the trip walks and teleports as needed.</summary>
    public void AutoPurchase()
    {
        if (!CanAutoPurchase)
            return;

        if (_logOpen())
            _closeLog(); // the crafting stance roots the character; the trip waits until it is free
        _trip.Start(BuyList!.Outstanding(), DateTime.UtcNow);
    }

    public bool CanAutoGather => GatherList is { AllDone: false } && !Busy && _odysseus.Available;

    public void AutoGather()
    {
        if (!CanAutoGather)
            return;

        if (_logOpen())
            _closeLog();
        _odysseus.Start(GatherList!.Outstanding());
    }

    public bool CanSend => GatherList is { AllDone: false } && _odysseus.Available && _odysseus.CanHandOver;

    public void SendToOdysseus()
    {
        if (CanSend)
            _odysseus.HandOver(GatherList!.Outstanding());
    }

    public void StopAll()
    {
        _trip.Stop();
        _retainerTrip.Stop();
        _chestTrip.Stop();
        _odysseus.Stop();
    }

    public void Tick(DateTime now)
    {
        _trip.Tick(now);
        _retainerTrip.Tick(now);
        _chestTrip.Tick(now);
        _odysseus.Tick(now);

        // A trip of ours just opened retainers or the chest: Charon's store has moved on, so has what we cached of it.
        if (_retainerTripWasActive && !_retainerTrip.Active)
            _charon.Invalidate();
        if (_chestTripWasActive && !_chestTrip.Active)
            _charonChest.Invalidate();
        _retainerTripWasActive = _retainerTrip.Active;
        _chestTripWasActive = _chestTrip.Active;

        if (!_bagsStale)
            return;

        _bagsStale = false;
        Gil = _gil();
        BuyList?.RefreshBags(_planner.Held);
        GatherList?.RefreshBags(_planner.Held);
    }
}
