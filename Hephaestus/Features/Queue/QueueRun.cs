using System;
using System.Collections.Generic;
using System.Linq;
using Hephaestus.Features.Crafting;
using Hephaestus.Features.Materials;

namespace Hephaestus.Features.Queue;

/// <summary>One thing a recipe consumes per craft — a material or a crystal, the sheet makes no difference.</summary>
public readonly record struct RecipeInput(uint ItemId, int Amount);

/// <summary>What the queue needs to know about a recipe. Built by an adapter from the game's sheets.</summary>
/// <param name="JobId">ClassJob row of the crafting job that makes it (8 = Carpenter ... 15 = Culinarian).</param>
/// <param name="Yield">Items per craft.</param>
/// <param name="JobLevel">The job level the recipe is meant for.</param>
public readonly record struct RecipeInfo(
    uint RecipeId, uint JobId, string Name, uint ResultItemId, IReadOnlyList<RecipeInput> Inputs, int Yield = 1, int JobLevel = 1);

/// <summary>What the game and the craft run look like right now, as far as the queue cares.</summary>
/// <param name="CanSwitchJob">Not in the crafting stance, not occupied, not in combat — a gearset change would be accepted.</param>
/// <param name="RunCompleted">Crafts the current run has finished so far.</param>
/// <param name="RunHalt">Why the run stopped, once <paramref name="RunActive"/> is false.</param>
/// <param name="FetchActive">The vendor trip / gathering run the queue asked for is still going.</param>
/// <param name="FetchNeedsPerson">It ended on something only a person can fix (bags full).</param>
/// <param name="FetchNotes">Why an item was not fetched, by item id, once it has ended.</param>
/// <param name="RunHaltReason">Why the run stopped, in words (<see cref="CraftRun.HaltReason"/>).</param>
public readonly record struct QueueObservation(
    uint CurrentJob,
    bool CanSwitchJob,
    bool LogOpen,
    bool RunActive,
    int RunCompleted,
    CraftRunHalt RunHalt,
    string RunStatus,
    bool FetchActive = false,
    bool FetchNeedsPerson = false,
    string FetchStatus = "",
    IReadOnlyDictionary<uint, string>? FetchNotes = null,
    string RunHaltReason = "");

public enum QueueCommandKind
{
    None,

    /// <summary>Close the crafting log — the game will not change job while it is open.</summary>
    CloseLog,
    SwitchJob,

    /// <summary>Start a <see cref="CraftRun"/> for a recipe and count.</summary>
    StartCraft,

    /// <summary>Forget the last run, so a halted one does not linger as "resumable" after its item was skipped.</summary>
    ClearRun,

    /// <summary>Go and get raw materials: buy them (<see cref="VendorTrip"/>) or have Odysseus gather them.</summary>
    Fetch,
}

public readonly record struct QueueCommand(QueueCommandKind Kind, uint JobId = 0, uint RecipeId = 0, int Count = 0, FetchRequest? Fetch = null)
{
    public static readonly QueueCommand None = new(QueueCommandKind.None);
}

public enum QueuePhase
{
    /// <summary>Not running: never started, finished, or halted (see <see cref="QueueRun.CanResume"/>).</summary>
    Idle,

    /// <summary>Choosing the next item and getting onto its job.</summary>
    Selecting,
    SwitchingJob,

    /// <summary>A <see cref="CraftRun"/> is making the current item.</summary>
    Crafting,

    /// <summary>Out buying or gathering raw materials, before any more crafting.</summary>
    Fetching,

    /// <summary>The list is done; closing the crafting log so whoever asked for it can move the character.</summary>
    Leaving,
}

/// <summary>
/// The queue engine: works through a <see cref="CraftList"/> unattended, one <see cref="CraftRun"/>
/// per item, changing job in between. Pure decision layer like CraftRun — it is handed an
/// observation and answers with at most one command.
///
/// The rule that makes it safe to leave alone: a problem with ONE ITEM never stops the queue, and a
/// problem that NEEDS A PERSON always does.
///   - out of materials, no gearset for the job, recipe will not open, synthesis will not start,
///     failed past the retry cap -> the item is flagged with the reason and skipped; the queue moves on.
///     A flagged item stays flagged until the player clears it: it is a request for review, not a retry loop.
///   - gear needs repair, a synthesis was cancelled, Stop was pressed -> the queue pauses where it
///     is. Progress is in the list (and on disk), so Resume simply carries on.
///
/// SUB-CRAFTS (when a <see cref="MaterialPlanner"/> is given): before every choice the list is
/// replanned against the bags. Intermediates it is short of, that this character can make, are
/// crafted FIRST — deepest first, as unlisted runs of their own — and only then the entries. They
/// are never written into the list: the plan is recomputed from what is actually held each time,
/// so a failed craft, a hand-made ingot or a Retry simply shows up in the next plan. An entry
/// with nothing craftable left is flagged with exactly what it is short of. A sub-craft that
/// would not run is not tried again until the queue is next started: what it would have made
/// becomes a shortfall of the entries that wanted it.
///
/// FETCHING (when outside sources are given): raw materials come FIRST, before any sub-craft —
/// everything to buy (a vendor trip), then everything left to gather (Odysseus). The plugin runs the
/// fetch; the queue judges it only by re-reading the bags. An item that did not arrive is struck off
/// that source until the queue is next started, so the next plan tries the next source (gathering
/// after a failed purchase) or names it as short — never a loop. Bags full pauses the queue.
///
/// LEAVING (a list started with <c>leaveWhenDone</c> — another plugin asked for it): the caller takes
/// the character back the moment the queue stops being active, and the crafting stance refuses every
/// step, mount and teleport. So when such a list has run out, the queue closes the crafting log and
/// STAYS ACTIVE until the game shows the stance has been left. The caller's own Stop
/// (<see cref="HandBack"/>) ends the same way, and never cuts the leaving short: Odysseus stops the
/// crafter the moment the items are in its bags, which is BEFORE the last synthesis has wound down. The
/// player's Stop and a pause that needs a person leave the log exactly as it is.
/// </summary>
public sealed class QueueRun
{
    public static readonly TimeSpan CommandPacing = TimeSpan.FromMilliseconds(600);

    /// <summary>How long a gearset change may take to show up as a new job.</summary>
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(6);

    /// <summary>After a job change, give stats and hotbars a moment before crafting on them.</summary>
    public static readonly TimeSpan AfterSwitchSettle = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a started run may take to report itself active.</summary>
    public static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(3);

    /// <summary>How long leaving the crafting stance may take, from the list running out to the character being free.</summary>
    public static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The character must LOOK free this long before it is believed: the log is away for a moment after a synthesis.</summary>
    public static readonly TimeSpan LeaveSettle = TimeSpan.FromMilliseconds(500);

    public const int MaxSwitchAttempts = 3;
    public const int MaxCloseAttempts = 8;

    private readonly Func<uint, RecipeInfo?> _recipeInfo;
    private readonly Func<uint, bool> _hasGearsetFor;
    private readonly MaterialPlanner? _materials;
    private readonly Func<bool>? _expandSubCrafts;
    private readonly Dictionary<uint, RecipeInfo?> _infoCache = new();
    private readonly Dictionary<uint, (uint ItemId, string Reason)> _blockedSubCrafts = new(); // by recipe
    private readonly Func<MaterialSources?>? _sources;
    private readonly Dictionary<uint, string> _blockedChest = new(); // by item
    private readonly Dictionary<uint, string> _blockedWithdrawals = new(); // by item
    private readonly Dictionary<uint, string> _blockedPurchases = new(); // by item
    private readonly Dictionary<uint, string> _blockedGathers = new();   // by item
    private readonly Dictionary<(FetchKind, uint), int> _fetches = new();
    private FetchRequest? _fetch;
    private bool _sawFetchActive;
    private bool _swept;
    private bool _sweptChest;

    private Action? _persist;
    private DateTime _nextCommandAt;
    private DateTime _deadline;
    private uint _wantedJob;
    private uint _workRecipeId;
    private PlannedSubCraft? _subCraft;
    private int _switchAttempts;
    private int _closeAttempts;
    private int _craftedAtStart;
    private bool _sawRunActive;
    private bool _leaveWhenDone;
    private string _doneStatus = string.Empty;
    private DateTime _freeSince;

    /// <param name="recipeInfo">Recipe lookup; null for a recipe the game does not know.</param>
    /// <param name="hasGearsetFor">Whether a gearset exists for a job. A missing gearset is a refusal, never an improvised gear swap.</param>
    /// <param name="materials">Plans sub-crafts and shortfalls. Absent = entries are crafted exactly as listed.</param>
    /// <param name="expandSubCrafts">The player's switch for it, read at every choice.</param>
    /// <param name="sources">Vendors and gathering, as they stand right now (null or absent parts = not used).</param>
    public QueueRun(
        Func<uint, RecipeInfo?> recipeInfo, Func<uint, bool> hasGearsetFor,
        MaterialPlanner? materials = null, Func<bool>? expandSubCrafts = null, Func<MaterialSources?>? sources = null)
    {
        _recipeInfo = recipeInfo;
        _hasGearsetFor = hasGearsetFor;
        _materials = materials;
        _expandSubCrafts = expandSubCrafts;
        _sources = sources;
    }

    /// <summary>A fetch may bring an item this many times per start; past that, something is eating it.</summary>
    public const int MaxFetchesPerItem = 4;

    public QueuePhase Phase { get; private set; } = QueuePhase.Idle;

    public CraftList? List { get; private set; }

    public CraftListItem? CurrentItem { get; private set; }

    /// <summary>The intermediate being made right now, when the run in progress is not a list entry.</summary>
    public PlannedSubCraft? CurrentSubCraft => Active ? _subCraft : null;

    /// <summary>What is being bought or gathered right now.</summary>
    public FetchRequest? CurrentFetch => Phase == QueuePhase.Fetching ? _fetch : null;

    /// <summary>Items finished / flagged since the queue was last started.</summary>
    public int ItemsCompleted { get; private set; }

    public int ItemsSkipped { get; private set; }

    public string Status { get; private set; } = "Idle — the queue has not been started.";

    public bool Active => Phase != QueuePhase.Idle;

    /// <summary>Halted with work left: <see cref="Resume"/> carries on from the list's own progress.</summary>
    public bool CanResume => !Active && List != null && HasRunnable(List);

    /// <param name="persist">Called whenever progress or a flag changes; null for a list that is not saved (an IPC request).</param>
    /// <param name="leaveWhenDone">Someone else takes the character afterwards: close the crafting log when the list has run out.</param>
    public void Start(CraftList list, Action? persist, bool leaveWhenDone = false)
    {
        List = list;
        _persist = persist;
        _leaveWhenDone = leaveWhenDone;
        ItemsCompleted = 0;
        ItemsSkipped = 0;
        _infoCache.Clear();
        Begin();
    }

    public void Resume()
    {
        if (CanResume)
            Begin();
    }

    /// <summary>Stop where it is. Progress is already in the list, so nothing is lost.</summary>
    public void Halt(string reason)
    {
        if (!Active)
            return;

        if (Phase == QueuePhase.Leaving)
        {
            // Nothing is left to pause: the work is done, only the log was still being closed.
            Phase = QueuePhase.Idle;
            Status = _doneStatus;
            return;
        }

        Phase = QueuePhase.Idle;
        Status = $"Paused{Where()} — {reason}";
    }

    private string Where()
        => _fetch != null ? " while fetching materials"
            : _subCraft != null ? $" at {_subCraft.Recipe.Name}"
            : CurrentItem != null && Info(CurrentItem.RecipeId) is { } info ? $" at {info.Name}"
            : string.Empty;

    /// <summary>
    /// Stop, said by whoever ASKED for this list: the work ends where it is, and the character is handed
    /// back able to move — the crafting log is closed first, and the queue stays active until it is. While
    /// the queue is already leaving, this changes nothing. For a list nobody else asked for it is a pause.
    /// </summary>
    public void HandBack(string reason, DateTime now)
    {
        if (!Active || Phase == QueuePhase.Leaving)
            return;

        if (!_leaveWhenDone)
        {
            Halt(reason);
            return;
        }

        Leave($"Stopped{Where()} — {reason}", now);
    }

    /// <summary>Forget the list entirely (the character changed, or an IPC request is finished with).</summary>
    public void Clear()
    {
        Phase = QueuePhase.Idle;
        List = null;
        CurrentItem = null;
        _subCraft = null;
        _fetch = null;
        _persist = null;
        Status = "Idle — the queue has not been started.";
    }

    public QueueCommand Tick(in QueueObservation obs, DateTime now) => Phase switch
    {
        QueuePhase.Selecting => TickSelecting(obs, now),
        QueuePhase.SwitchingJob => TickSwitching(obs, now),
        QueuePhase.Crafting => TickCrafting(obs, now),
        QueuePhase.Fetching => TickFetching(obs, now),
        QueuePhase.Leaving => TickLeaving(obs, now),
        _ => QueueCommand.None,
    };

    private void Begin()
    {
        _nextCommandAt = default;
        _switchAttempts = 0;
        _closeAttempts = 0;
        _workRecipeId = 0;
        CurrentItem = null;
        _subCraft = null;
        // A person pressed Start or Resume: whatever was wrong may have been put right.
        _blockedSubCrafts.Clear();
        _blockedChest.Clear();
        _blockedWithdrawals.Clear();
        _blockedPurchases.Clear();
        _blockedGathers.Clear();
        _swept = false;
        _sweptChest = false;
        _fetches.Clear();
        _fetch = null;
        Phase = QueuePhase.Selecting;
        Status = "Choosing the next item";
    }

    private bool Planning => _materials != null && List != null && _expandSubCrafts?.Invoke() != false;

    private QueueCommand TickSelecting(in QueueObservation obs, DateTime now)
    {
        if (now < _nextCommandAt)
            return QueueCommand.None;

        MaterialPlan? plan = null;
        if (Planning)
        {
            var (vendors, gathering, retainers, chest) = Sources();
            plan = _materials!.Plan(List!.Items, obs.CurrentJob, _blockedSubCrafts.Keys, vendors, gathering, retainers, chest);

            // Materials first — chest, retainers, buy, then gather — so every sub-craft after them has what it needs.
            if (FetchFrom(plan) is { } request)
                return Fetch(request, obs, now);

            // Still short, and the chest has never been seen: one look, then replan.
            if (plan.Shortfalls.Count > 0 && !_sweptChest && chest != null && !chest.Known())
            {
                _sweptChest = true;
                var look = new FetchRequest(
                    FetchKind.ChestWithdraw,
                    plan.Shortfalls.Select(s => new FetchItem(s.ItemId, s.Name, _materials.Held(s.ItemId) + s.Missing, s.Missing)).ToList(),
                    Sweep: true);
                return Fetch(look, obs, now);
            }

            // Still short, and retainers exist whose bags were never seen: look once, then replan.
            if (plan.Shortfalls.Count > 0 && !_swept && retainers?.NeverIndexed() > 0)
            {
                _swept = true;
                var sweep = new FetchRequest(
                    FetchKind.Withdraw,
                    plan.Shortfalls.Select(s => new FetchItem(s.ItemId, s.Name, _materials.Held(s.ItemId) + s.Missing, s.Missing)).ToList(),
                    Sweep: true);
                return Fetch(sweep, obs, now);
            }
        }

        // Then intermediates: whatever the list is short of and this character can make.
        if (plan != null && NextSubCraft(plan, obs.CurrentJob) is { } ready)
            return Work(ready.SubCraft.Recipe, ready.Crafts, entry: null, ready.SubCraft, obs, now);

        var item = SelectNext(obs.CurrentJob);
        if (item == null)
        {
            CurrentItem = null;
            _subCraft = null;
            var done = ItemsSkipped == 0
                ? $"Done — {ItemsCompleted} item(s) crafted."
                : $"Done — {ItemsCompleted} item(s) crafted, {ItemsSkipped} flagged for review.";
            if (_leaveWhenDone)
            {
                Leave(done, now);
                return TickLeaving(obs, now);
            }

            return Finished(done);
        }

        if (Info(item.RecipeId) is not { } info)
            return Skip(item, "the game does not know this recipe");

        var count = item.Quantity - item.Crafted;
        if (plan != null)
        {
            // Every sub-craft that could run has run: what the bags make now is all this entry can get.
            count = Math.Min(count, _materials!.CraftableNow(info));
            if (count == 0)
                return Skip(item, ShortOf(plan, item));
        }

        return Work(info, count, item, subCraft: null, obs, now);
    }

    /// <summary>
    /// The list has run out and someone else is waiting for the character: close the crafting log and stay
    /// active until the stance has really been left — read off the game, never assumed from the click. The
    /// work itself is done whatever happens here, so a log that will not close ends the queue all the same,
    /// and says so.
    /// </summary>
    private void Leave(string outcome, DateTime now)
    {
        _doneStatus = outcome;
        _closeAttempts = 0;
        _freeSince = default;
        _nextCommandAt = default;
        _deadline = now + LeaveTimeout;
        CurrentItem = null;
        _subCraft = null;
        _fetch = null;
        Phase = QueuePhase.Leaving;
        Status = $"{outcome} Leaving the crafting stance.";
    }

    private QueueCommand TickLeaving(in QueueObservation obs, DateTime now)
    {
        if (!obs.LogOpen && obs.CanSwitchJob)
        {
            if (_freeSince == default)
                _freeSince = now;
            return now - _freeSince >= LeaveSettle ? Finished(_doneStatus) : QueueCommand.None;
        }

        _freeSince = default;
        if (now > _deadline)
            return Finished($"{_doneStatus} The character is still in the crafting stance — it could not be left in time.");

        // Log away (the last synthesis is still winding down, or the tools are being put away): wait for the game.
        if (!obs.LogOpen || now < _nextCommandAt)
            return QueueCommand.None;

        if (++_closeAttempts > MaxCloseAttempts)
            return Finished($"{_doneStatus} The character is still in the crafting stance — the crafting log would not close.");

        _nextCommandAt = now + CommandPacing;
        Status = $"{_doneStatus} Closing the crafting log, so the character is free to move.";
        return new QueueCommand(QueueCommandKind.CloseLog);
    }

    private QueueCommand Finished(string status)
    {
        Phase = QueuePhase.Idle;
        Status = status;
        return QueueCommand.None;
    }

    /// <summary>Get onto the job that makes it, then start the run — one road for a list entry and for a sub-craft.</summary>
    private QueueCommand Work(
        in RecipeInfo info, int count, CraftListItem? entry, PlannedSubCraft? subCraft, in QueueObservation obs, DateTime now)
    {
        if (info.RecipeId != _workRecipeId)
        {
            _workRecipeId = info.RecipeId;
            _switchAttempts = 0;
            _closeAttempts = 0;
        }

        CurrentItem = entry;
        _subCraft = subCraft;

        if (info.JobId != obs.CurrentJob)
        {
            if (!_hasGearsetFor(info.JobId))
                return GiveUp("no gearset saved for the job that makes it");

            if (obs.LogOpen)
            {
                if (++_closeAttempts > MaxCloseAttempts)
                {
                    Halt("the crafting log would not close for a job change");
                    return QueueCommand.None;
                }

                _nextCommandAt = now + CommandPacing;
                Status = $"Closing the crafting log to change job for {info.Name}";
                return new QueueCommand(QueueCommandKind.CloseLog);
            }

            if (!obs.CanSwitchJob)
            {
                _nextCommandAt = now + CommandPacing; // every pass replans: not once a frame while we wait
                Status = $"Waiting to be able to change job for {info.Name}";
                return QueueCommand.None;
            }

            _wantedJob = info.JobId;
            _deadline = now + SwitchTimeout;
            Phase = QueuePhase.SwitchingJob;
            Status = $"Changing job for {info.Name}";
            return new QueueCommand(QueueCommandKind.SwitchJob, JobId: info.JobId);
        }

        _craftedAtStart = entry?.Crafted ?? 0;
        _sawRunActive = false;
        _deadline = now + StartGrace;
        Phase = QueuePhase.Crafting;
        Status = subCraft == null
            ? $"Crafting {info.Name}"
            : $"Making {count} × {info.Name} first, for {string.Join(", ", subCraft.For.Select(NameOf))}";
        return new QueueCommand(QueueCommandKind.StartCraft, RecipeId: info.RecipeId, Count: count);
    }

    /// <summary>The outside sources, minus what has already failed to arrive from each since the last start.</summary>
    private (VendorSupply? Vendors, GatherSupply? Gathering, RetainerSupply? Retainers, ChestSupply? Chest) Sources()
    {
        var sources = _sources?.Invoke();
        var chest = sources?.Chest is { } c
            ? c with { Held = id => _blockedChest.ContainsKey(id) ? 0 : c.Held(id) }
            : null;
        var retainers = sources?.Retainers is { } r
            ? r with { Held = id => _blockedWithdrawals.ContainsKey(id) ? 0 : r.Held(id) }
            : null;
        var vendors = sources?.Vendors is { } v
            ? v with { UnitPrice = id => _blockedPurchases.ContainsKey(id) ? 0 : v.UnitPrice(id) }
            : null;
        var gathering = sources?.Gathering is { } g
            ? new GatherSupply(id => !_blockedGathers.ContainsKey(id) && g.CanGather(id))
            : null;
        return (vendors, gathering, retainers, chest);
    }

    /// <summary>Everything to buy, else everything to gather — each up to a bag count, sized from the whole list.</summary>
    private FetchRequest? FetchFrom(MaterialPlan plan)
    {
        if (plan.ChestWithdrawals.Count > 0)
            return new FetchRequest(FetchKind.ChestWithdraw, plan.ChestWithdrawals.Select(w => Item(w.ItemId, w.Name, w.Quantity)).ToList());
        if (plan.Withdrawals.Count > 0)
            return new FetchRequest(FetchKind.Withdraw, plan.Withdrawals.Select(w => Item(w.ItemId, w.Name, w.Quantity)).ToList());
        if (plan.Purchases.Count > 0)
            return new FetchRequest(FetchKind.Buy, plan.Purchases.Select(p => Item(p.ItemId, p.Name, p.Quantity)).ToList());
        if (plan.Gathers.Count > 0)
            return new FetchRequest(FetchKind.Gather, plan.Gathers.Select(g => Item(g.ItemId, g.Name, g.Quantity)).ToList());
        return null;

        FetchItem Item(uint itemId, string name, int quantity) => new(itemId, name, _materials!.Held(itemId) + quantity, quantity);
    }

    /// <summary>Out of the crafting stance, then hand the request to the plugin.</summary>
    private QueueCommand Fetch(FetchRequest request, in QueueObservation obs, DateTime now)
    {
        CurrentItem = null;
        _subCraft = null;
        _workRecipeId = 0;

        // Something keeps coming back short however often it is fetched: stop asking, name it.
        var exhausted = request.Items.Where(i => _fetches.GetValueOrDefault((request.Kind, i.ItemId)) >= MaxFetchesPerItem).ToList();
        if (exhausted.Count > 0)
        {
            foreach (var item in exhausted)
                Blocked(request.Kind)[item.ItemId] = $"fetched {MaxFetchesPerItem} times and still short";
            return QueueCommand.None; // replanned on the next tick, without them
        }

        var what = request.Kind switch { FetchKind.ChestWithdraw => "take from the company chest", FetchKind.Withdraw => "withdraw", FetchKind.Buy => "buy", _ => "gather" };
        if (obs.LogOpen)
        {
            if (++_closeAttempts > MaxCloseAttempts)
            {
                Halt($"the crafting log would not close to go and {what} materials");
                return QueueCommand.None;
            }

            _nextCommandAt = now + CommandPacing;
            Status = $"Closing the crafting log to {what} materials";
            return new QueueCommand(QueueCommandKind.CloseLog);
        }

        if (!obs.CanSwitchJob)
        {
            _nextCommandAt = now + CommandPacing;
            Status = $"Waiting to be free to move, to {what} materials";
            return QueueCommand.None;
        }

        _closeAttempts = 0;
        foreach (var item in request.Items)
            _fetches[(request.Kind, item.ItemId)] = _fetches.GetValueOrDefault((request.Kind, item.ItemId)) + 1;

        _fetch = request;
        _sawFetchActive = false;
        _deadline = now + StartGrace;
        Phase = QueuePhase.Fetching;
        var names = string.Join(", ", request.Items.Select(i => $"{i.Quantity} × {i.Name}"));
        Status = request.Kind switch
        {
            FetchKind.ChestWithdraw => request.Sweep ? $"Looking in the company chest for {names}" : $"Taking {names} from the company chest",
            FetchKind.Withdraw => request.Sweep ? $"Looking through unindexed retainers for {names}" : $"Withdrawing {names} from retainers",
            FetchKind.Buy => $"Buying {names}",
            _ => $"Asking Odysseus to gather {names}",
        };
        return new QueueCommand(QueueCommandKind.Fetch, Fetch: request);
    }

    /// <summary>The fetch, judged by the bags — never by what the fetcher says about itself.</summary>
    private QueueCommand TickFetching(in QueueObservation obs, DateTime now)
    {
        if (_fetch is not { } fetch)
        {
            Phase = QueuePhase.Selecting;
            return QueueCommand.None;
        }

        if (obs.FetchActive)
        {
            _sawFetchActive = true;
            if (obs.FetchStatus.Length > 0)
                Status = fetch.Kind switch { FetchKind.ChestWithdraw => "Company chest: ", FetchKind.Withdraw => "Retainers: ", FetchKind.Buy => "Buying materials: ", _ => "Gathering (Odysseus): " } + obs.FetchStatus;
            return QueueCommand.None;
        }

        // Asked this very tick; it reports itself on the next one. If it never does, it refused.
        if (!_sawFetchActive && now <= _deadline)
            return QueueCommand.None;

        _fetch = null;
        if (_sawFetchActive && obs.FetchNeedsPerson)
        {
            Halt(obs.FetchStatus.Length > 0 ? obs.FetchStatus : "fetching materials needs you");
            return QueueCommand.None;
        }

        foreach (var item in fetch.Items)
        {
            if (_materials!.Held(item.ItemId) >= item.Target)
                continue;

            Blocked(fetch.Kind)[item.ItemId] =
                !_sawFetchActive ? fetch.Kind switch
                    {
                        FetchKind.ChestWithdraw => "the chest trip would not start",
                        FetchKind.Withdraw => "the retainer trip would not start",
                        FetchKind.Buy => "the vendor trip would not start",
                        _ => "Odysseus would not start gathering it",
                    }
                : obs.FetchNotes?.GetValueOrDefault(item.ItemId) is { Length: > 0 } note ? note
                : obs.FetchStatus.Length > 0 ? obs.FetchStatus
                : "it did not arrive";
        }

        Phase = QueuePhase.Selecting;
        _nextCommandAt = now + CommandPacing; // let the bags settle before replanning on them
        return QueueCommand.None;
    }

    private Dictionary<uint, string> Blocked(FetchKind kind)
        => kind switch { FetchKind.ChestWithdraw => _blockedChest, FetchKind.Withdraw => _blockedWithdrawals, FetchKind.Buy => _blockedPurchases, _ => _blockedGathers };

    /// <summary>
    /// The next intermediate that can be made NOW. Deepest first; among equally deep ones, the job we
    /// are on. (Not simply "the current job first": topping up a part before its ingots exist would
    /// mean coming back to it, which costs two gearset changes instead of none.)
    /// </summary>
    private (PlannedSubCraft SubCraft, int Crafts)? NextSubCraft(MaterialPlan plan, uint currentJob)
    {
        (PlannedSubCraft SubCraft, int Crafts)? first = null;
        foreach (var subCraft in plan.SubCrafts)
        {
            if (first != null && subCraft.Depth != first.Value.SubCraft.Depth)
                break;

            var crafts = Math.Min(subCraft.Crafts, _materials!.CraftableNow(subCraft.Recipe));
            if (crafts <= 0)
                continue;

            if (subCraft.Recipe.JobId == currentJob)
                return (subCraft, crafts);

            first ??= (subCraft, crafts);
        }

        return first;
    }

    /// <summary>Why an entry cannot go on, in terms a person can act on: the materials, not the symptom.</summary>
    private string ShortOf(MaterialPlan plan, CraftListItem item)
    {
        var parts = plan.ShortfallsFor(item).Select(s =>
        {
            var why = new List<string>();
            var blocked = _blockedSubCrafts.Values.FirstOrDefault(b => b.ItemId == s.ItemId);
            if (blocked.Reason != null)
                why.Add($"could not be made: {blocked.Reason}");
            if (_blockedChest.TryGetValue(s.ItemId, out var notFromChest))
                why.Add($"not taken from the company chest: {notFromChest}");
            if (_blockedWithdrawals.TryGetValue(s.ItemId, out var notWithdrawn))
                why.Add($"not withdrawn: {notWithdrawn}");
            if (_blockedPurchases.TryGetValue(s.ItemId, out var notBought))
                why.Add($"not bought: {notBought}");
            if (_blockedGathers.TryGetValue(s.ItemId, out var notGathered))
                why.Add($"not gathered: {notGathered}");
            return $"{s.ByEntry[item]} × {s.Name}" + (why.Count == 0 ? string.Empty : $" ({string.Join("; ", why)})");
        }).ToList();

        return parts.Count == 0 ? "out of materials" : $"short of {string.Join(", ", parts)}";
    }

    private string NameOf(CraftListItem entry) => Info(entry.RecipeId) is { } info ? info.Name : $"recipe {entry.RecipeId}";

    private QueueCommand TickSwitching(in QueueObservation obs, DateTime now)
    {
        if (obs.CurrentJob == _wantedJob)
        {
            _nextCommandAt = now + AfterSwitchSettle;
            Phase = QueuePhase.Selecting;
            return QueueCommand.None;
        }

        if (now <= _deadline)
            return QueueCommand.None;

        if (++_switchAttempts >= MaxSwitchAttempts && (CurrentItem != null || _subCraft != null))
            return GiveUp("could not change to the job that makes it");

        _nextCommandAt = now + CommandPacing;
        Phase = QueuePhase.Selecting;
        return QueueCommand.None;
    }

    private QueueCommand TickCrafting(in QueueObservation obs, DateTime now)
    {
        if (_subCraft != null)
            return TickSubCraft(obs, now);

        if (CurrentItem is not { } item)
        {
            Phase = QueuePhase.Selecting;
            return QueueCommand.None;
        }

        if (obs.RunActive)
        {
            _sawRunActive = true;
            Record(item, obs.RunCompleted);
            return QueueCommand.None;
        }

        if (!_sawRunActive)
        {
            // Started this very tick; it reports active on the next one. If it never does, it refused to start.
            return now <= _deadline ? QueueCommand.None : Skip(item, "the craft run would not start");
        }

        Record(item, obs.RunCompleted);

        switch (obs.RunHalt)
        {
            case CraftRunHalt.None:
                // The run did everything asked of it (or was cleared): onward. An item that somehow
                // is not complete yet is simply picked again.
                if (item.IsComplete)
                    ItemsCompleted++;
                Phase = QueuePhase.Selecting;
                return QueueCommand.None;

            // A problem with this ITEM: flag it and keep going.
            case CraftRunHalt.OutOfMaterials:
                return Skip(item, "out of materials");
            case CraftRunHalt.FailedRepeatedly:
                return Skip(item, "the synthesis kept failing");
            case CraftRunHalt.WouldNotStart:
                return Skip(item, "the synthesis would not start — stats too low, or the recipe's requirements are not met");
            case CraftRunHalt.LogUnresponsive:
                return Skip(item, "the crafting log would not open the recipe — not unlocked?");
            case CraftRunHalt.MaterialChoice:
                return Skip(item, MaterialChoice(obs));

            // Something that needs a person: stop here, resumable.
            default:
                Halt(obs.RunStatus);
                return QueueCommand.None;
        }
    }

    /// <summary>
    /// A sub-craft's run, judged. It has no list entry to record into or to flag: success or a short
    /// run simply goes back to planning, which sees what is really in the bags now; a run that
    /// would not work blocks the recipe so the plan stops asking for it.
    /// </summary>
    private QueueCommand TickSubCraft(in QueueObservation obs, DateTime now)
    {
        if (obs.RunActive)
        {
            _sawRunActive = true;
            return QueueCommand.None;
        }

        if (!_sawRunActive)
            return now <= _deadline ? QueueCommand.None : GiveUp("the craft run would not start");

        switch (obs.RunHalt)
        {
            case CraftRunHalt.None:
                Phase = QueuePhase.Selecting;
                return QueueCommand.None;

            case CraftRunHalt.OutOfMaterials:
                // We counted the bags and thought it could be made; the game disagreed. Made some: fine,
                // replan. Made none: never ask again, or this is a loop.
                if (obs.RunCompleted == 0)
                    return GiveUp("the game says the materials are not there");
                Phase = QueuePhase.Selecting;
                return new QueueCommand(QueueCommandKind.ClearRun);

            case CraftRunHalt.FailedRepeatedly:
                return GiveUp("the synthesis kept failing");
            case CraftRunHalt.WouldNotStart:
                return GiveUp("the synthesis would not start");
            case CraftRunHalt.LogUnresponsive:
                return GiveUp("the crafting log would not open the recipe — not unlocked?");
            case CraftRunHalt.MaterialChoice:
                return GiveUp(MaterialChoice(obs));

            default:
                Halt(obs.RunStatus);
                return QueueCommand.None;
        }
    }

    /// <summary>
    /// The next item to make. In list order — except that, unless the list is pinned, an item for
    /// the job we are ALREADY on may go first to save a gearset change, provided nothing still ahead
    /// of it in the queue makes one of its ingredients (so a sub-craft is never overtaken by what
    /// needs it).
    /// </summary>
    private CraftListItem? SelectNext(uint currentJob)
    {
        if (List == null)
            return null;

        CraftListItem? first = null;
        var ahead = new List<uint>(); // result items of runnable entries before the candidate
        foreach (var item in List.Items)
        {
            if (!item.IsRunnable)
                continue;

            first ??= item;
            if (List.KeepOrder)
                return first;

            if (Info(item.RecipeId) is not { } info)
            {
                continue;
            }

            if (info.JobId == currentJob && !NeedsAnyOf(info, ahead))
                return item;

            ahead.Add(info.ResultItemId);
        }

        return first;
    }

    private static bool NeedsAnyOf(in RecipeInfo info, List<uint> resultsAhead)
    {
        foreach (var ingredient in info.Inputs)
        {
            if (resultsAhead.Contains(ingredient.ItemId))
                return true;
        }

        return false;
    }

    private void Record(CraftListItem item, int runCompleted)
    {
        var crafted = _craftedAtStart + runCompleted;
        if (crafted == item.Crafted)
            return;

        item.Crafted = crafted;
        _persist?.Invoke();
    }

    /// <summary>Whatever is being worked on cannot be made: flag the entry, or stop planning the sub-craft.</summary>
    private QueueCommand GiveUp(string reason)
    {
        if (_subCraft is { } subCraft)
        {
            _blockedSubCrafts[subCraft.Recipe.RecipeId] = (subCraft.Recipe.ResultItemId, reason);
            _subCraft = null;
            _workRecipeId = 0;
            Phase = QueuePhase.Selecting;
            Status = $"Cannot make {subCraft.Recipe.Name}: {reason}";
            return new QueueCommand(QueueCommandKind.ClearRun);
        }

        if (CurrentItem is { } item)
            return Skip(item, reason);

        Phase = QueuePhase.Selecting;
        return QueueCommand.None;
    }

    private QueueCommand Skip(CraftListItem item, string reason)
    {
        item.SkipReason = reason;
        _persist?.Invoke();
        ItemsSkipped++;
        CurrentItem = null;
        _workRecipeId = 0;
        Phase = QueuePhase.Selecting;
        Status = $"Flagged {(Info(item.RecipeId) is { } info ? info.Name : $"recipe {item.RecipeId}")}: {reason}";
        return new QueueCommand(QueueCommandKind.ClearRun);
    }

    private static string MaterialChoice(in QueueObservation obs)
        => obs.RunHaltReason.Length > 0 ? obs.RunHaltReason : "a piece of gear it uses up has to be chosen by hand";

    private RecipeInfo? Info(uint recipeId)
    {
        if (!_infoCache.TryGetValue(recipeId, out var info))
            _infoCache[recipeId] = info = _recipeInfo(recipeId);
        return info;
    }

    private static bool HasRunnable(CraftList list)
    {
        foreach (var item in list.Items)
        {
            if (item.IsRunnable)
                return true;
        }

        return false;
    }
}
