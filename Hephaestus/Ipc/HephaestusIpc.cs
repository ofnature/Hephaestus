using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Hephaestus.Ipc;

/// <summary>
/// What Hephaestus publishes for other plugins.
///
/// <para>
/// <b><c>Hephaestus.IsBusy</c></b> → <c>bool</c> — true while Hephaestus is driving the character
/// (the queue, between its items too, a craft run, a trip or gathering it asked Demeter for, a synthesis
/// the solver is pressing in Auto) or still handing it back after <c>RequestStopAfterRun</c>. Same shape
/// as <c>Theseus.IsBusy</c> (and Henchman.IsBusy / AutoDuty.IsStopped), so Daedalus's
/// AutomationBusyBridge and any orchestrator can poll it with no new mechanism. Level-triggered: it reflects the truth right now rather than latching. If
/// Hephaestus is unloaded mid-run the gate stops existing, which consumers read as idle.
/// </para>
///
/// <para>
/// <b><c>Hephaestus.CraftItem(uint recipeId, int amount)</c></b> → <c>bool</c> — make
/// <c>amount</c> of a recipe, with everything the queue does for its own items: change to the job
/// that makes it, solve, craft, verify each item arrived. Mirrors <c>Artisan.CraftItem</c>, so
/// Odysseus's <c>Deliveries.ICrafter</c> gets a drop-in. True means the work BEGAN — <c>IsBusy</c>
/// is already true when this returns, then falls when it ends, whichever way; the caller judges
/// the outcome from its own inventory, exactly as it does with Artisan. Like the player's own queue,
/// it makes missing intermediates first when it can (so asking for the deepest sub-craft first, as
/// an Artisan caller must, still works — it just is no longer necessary). False (nothing started):
/// not logged in, unknown recipe, amount below 1, or Hephaestus already busy.
/// When the request runs out by itself (made, or flagged) Hephaestus CLOSES THE CRAFTING LOG and
/// <c>IsBusy</c> stays true until the character has left the crafting stance, so the caller can walk,
/// mount or teleport the moment it falls. <c>Stop</c> called on such a request ends the same way — the work
/// stops, the log is closed, then <c>IsBusy</c> falls — because a caller stops the crafter as soon as it has
/// what it needs, which is before the last synthesis has wound down. A pause that needs a person leaves
/// the log as it is. The same holds for <c>CraftList</c>.
/// </para>
///
/// <para>
/// <b><c>Hephaestus.CraftList(string json)</c></b> → <c>bool</c> — the same as <c>CraftItem</c>, for a whole
/// custom list built by the caller: a one-off, unsaved list run through the queue engine, in the
/// order given (sub-crafts first, if the caller put them first). Format and rules:
/// <see cref="Features.Queue.ListRequest"/> — entries name a <c>recipeId</c> or an <c>itemId</c>, and the
/// request is ALL OR NOTHING: one bad entry and nothing starts (the reason goes to /xllog).
/// <b><c>Hephaestus.AddToQueue(string json)</c></b> → <c>bool</c> — same format, but the items are
/// appended to the PLAYER'S saved queue and nothing is started. Works while the queue is running.
/// <b><c>Hephaestus.StartQueue</c></b> → <c>bool</c> — start (or resume) the player's saved queue; true when
/// it is running as the call returns.
/// </para>
///
/// <para>
/// <b><c>Hephaestus.CraftSelected(int amount)</c></b> → <c>bool</c> — make <c>amount</c> of THE RECIPE THAT IS
/// SELECTED IN THE OPEN LOG, where it is: the ordinary crafting log or the Cosmic Crafting Log of a Cosmic
/// Exploration mission (added 2026-11-12 for Selene - a mission's recipe exists only in that log, which
/// <c>CraftItem</c> cannot open). It is the "Craft X" button as a gate: Synthesize is pressed again and again, each
/// item made by the solver; never more than the log says the materials make. True means the run BEGAN and
/// <c>IsBusy</c> is already true; it falls when the run ends - made, halted, or refused by the game (a mission that
/// is complete takes no more). Unlike <c>CraftItem</c> the log is LEFT OPEN: it is the caller's, and the caller
/// decides what comes next. A run left halted from before is forgotten. False when no log is open, nothing is
/// selected, <c>amount</c> is below 1, nothing can be made, or Hephaestus is busy (a synthesis already in progress
/// counts). A recipe for ANOTHER JOB is made too, the way the Craft X button makes one: it becomes a
/// <c>CraftItem</c> request - the job is changed, the amount is not capped by what the log said, and it ends as
/// <c>CraftItem</c> ends (log closed, <c>IsBusy</c> until the crafting stance is left).
/// <c>Stop</c> and <c>RequestStopAfterRun</c> end it like any run. In Assisted mode the player presses the actions.
/// <c>CraftItem</c> itself reaches a Cosmic mission's recipe as well (the agent opens the Cosmic log for it; with
/// that log open the recipe is picked from its list) - the job change included. NOT seen in game when written.
/// </para>
///
/// <para>
/// <b><c>Hephaestus.ChangeJob(uint classJobId)</c></b> → <c>bool</c> — put the character on a job (added
/// 2026-11-12 for Selene: a Cosmic mission belongs to a job, and the job has to be right BEFORE the mission is
/// accepted - before there is anything to craft). The careful change the queue makes between its items, on
/// request: close the crafting log if one is open, wait until the game would accept it, equip the saved
/// gearset, watch the job actually change, let the stats settle. True = already on that job (nothing to do,
/// <c>IsBusy</c> untouched), or the change BEGAN and <c>IsBusy</c> is true until it has ended - on the job, or
/// given up. The caller reads the job itself once <c>IsBusy</c> has fallen: that is the verdict. False, and
/// nothing changed: not logged in, no gearset saved for the job, or Hephaestus busy with something else. Any
/// job with a saved gearset, not only the crafting ones. <c>Stop</c> drops it.
/// </para>
///
/// <para>
/// <b><c>Hephaestus.Stop</c></b> — stop whatever is running after the current action. Safe to call when idle.
/// <b><c>Hephaestus.RequestStopAfterRun</c></b> → <c>bool</c> — for a plugin about to take the character
/// elsewhere (Argus): finish the synthesis in progress (never abandoned), then start nothing more — no
/// next craft, no next item, no auto-resume — and close the crafting log; <c>IsBusy</c> stays true until
/// the character is out of the crafting stance. Trips (buying, retainers, chest, gathering) stop at once.
/// True when it was busy and will stop (or already is stopping); false, and nothing changed, when idle.
/// Idempotent. A paused queue or run stays resumable by hand; a fresh start clears the request.
/// <b><c>Hephaestus.GetQueueStatusJson</c></b> → <c>string</c> — see <see cref="QueueStatusDto"/>; <c>"{}"</c> on any error, never a throw.
/// <b><c>Hephaestus.GetVersion</c></b> → <c>string</c>.
/// </para>
///
/// <para>
/// Contract rule (same as Charon's gates and the Daedalus LAN schema): EXTEND-ONLY. A published
/// gate never changes its signature or meaning; new behaviour gets a new gate, new status fields
/// are only ever added. No gate may throw into a caller's poll.
/// </para>
/// </summary>
public sealed class HephaestusIpc : IDisposable
{
    public const string IsBusyGate = "Hephaestus.IsBusy";
    public const string GetVersionGate = "Hephaestus.GetVersion";
    public const string CraftItemGate = "Hephaestus.CraftItem";
    public const string StopGate = "Hephaestus.Stop";
    public const string GetQueueStatusJsonGate = "Hephaestus.GetQueueStatusJson";
    public const string CraftListGate = "Hephaestus.CraftList";
    public const string AddToQueueGate = "Hephaestus.AddToQueue";
    public const string StartQueueGate = "Hephaestus.StartQueue";
    public const string RequestStopAfterRunGate = "Hephaestus.RequestStopAfterRun";
    public const string CraftSelectedGate = "Hephaestus.CraftSelected";
    public const string ChangeJobGate = "Hephaestus.ChangeJob";

    public static readonly string[] Gates =
    {
        IsBusyGate, CraftItemGate, CraftSelectedGate, CraftListGate, AddToQueueGate, StartQueueGate, ChangeJobGate, StopGate,
        RequestStopAfterRunGate, GetQueueStatusJsonGate, GetVersionGate,
    };

    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<string> _getVersion;
    private readonly ICallGateProvider<uint, int, bool> _craftItem;
    private readonly ICallGateProvider<object?> _stop;
    private readonly ICallGateProvider<string> _getQueueStatusJson;
    private readonly ICallGateProvider<string, bool> _craftList;
    private readonly ICallGateProvider<string, bool> _addToQueue;
    private readonly ICallGateProvider<bool> _startQueue;
    private readonly ICallGateProvider<bool> _requestStopAfterRun;
    private readonly ICallGateProvider<int, bool> _craftSelected;
    private readonly ICallGateProvider<uint, bool> _changeJob;

    /// <remarks>Every delegate is wrapped: an exception in ours must never surface inside another plugin.</remarks>
    public HephaestusIpc(
        IDalamudPluginInterface pluginInterface,
        Func<bool> isBusy,
        Func<uint, int, bool> craftItem,
        Action stop,
        Func<string> queueStatusJson,
        Func<string, bool> craftList,
        Func<string, bool> addToQueue,
        Func<bool> startQueue,
        Func<bool> requestStopAfterRun,
        Func<int, bool> craftSelected,
        Func<uint, bool> changeJob)
    {
        _isBusy = pluginInterface.GetIpcProvider<bool>(IsBusyGate);
        _isBusy.RegisterFunc(() => Guard(isBusy, fallback: false)); // fail open to idle, as an absent gate reads

        _getVersion = pluginInterface.GetIpcProvider<string>(GetVersionGate);
        _getVersion.RegisterFunc(() => HephaestusPlugin.PluginVersion);

        _craftItem = pluginInterface.GetIpcProvider<uint, int, bool>(CraftItemGate);
        _craftItem.RegisterFunc((recipeId, amount) => Guard(() => craftItem(recipeId, amount), fallback: false));

        _stop = pluginInterface.GetIpcProvider<object?>(StopGate);
        _stop.RegisterAction(() => Guard(() => { stop(); return true; }, fallback: false));

        _getQueueStatusJson = pluginInterface.GetIpcProvider<string>(GetQueueStatusJsonGate);
        _getQueueStatusJson.RegisterFunc(() => Guard(queueStatusJson, fallback: "{}"));

        _craftList = pluginInterface.GetIpcProvider<string, bool>(CraftListGate);
        _craftList.RegisterFunc(json => Guard(() => craftList(json), fallback: false));

        _addToQueue = pluginInterface.GetIpcProvider<string, bool>(AddToQueueGate);
        _addToQueue.RegisterFunc(json => Guard(() => addToQueue(json), fallback: false));

        _startQueue = pluginInterface.GetIpcProvider<bool>(StartQueueGate);
        _startQueue.RegisterFunc(() => Guard(startQueue, fallback: false));

        _requestStopAfterRun = pluginInterface.GetIpcProvider<bool>(RequestStopAfterRunGate);
        _requestStopAfterRun.RegisterFunc(() => Guard(requestStopAfterRun, fallback: false));

        _craftSelected = pluginInterface.GetIpcProvider<int, bool>(CraftSelectedGate);
        _craftSelected.RegisterFunc(amount => Guard(() => craftSelected(amount), fallback: false));

        _changeJob = pluginInterface.GetIpcProvider<uint, bool>(ChangeJobGate);
        _changeJob.RegisterFunc(jobId => Guard(() => changeJob(jobId), fallback: false));
    }

    public void Dispose()
    {
        _isBusy.UnregisterFunc();
        _getVersion.UnregisterFunc();
        _craftItem.UnregisterFunc();
        _stop.UnregisterAction();
        _getQueueStatusJson.UnregisterFunc();
        _craftList.UnregisterFunc();
        _addToQueue.UnregisterFunc();
        _startQueue.UnregisterFunc();
        _requestStopAfterRun.UnregisterFunc();
        _craftSelected.UnregisterFunc();
        _changeJob.UnregisterFunc();
    }

    private static T Guard<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>
/// The shape of <c>Hephaestus.GetQueueStatusJson</c>. EXTEND-ONLY: fields are added, never renamed
/// or repurposed — consumers deserialize leniently and ignore what they do not know.
/// </summary>
public sealed class QueueStatusDto
{
    /// <summary>The queue (or a craft run) is driving the character right now.</summary>
    public bool Active { get; set; }

    /// <summary>Halted with work left; a person (or <c>CraftItem</c> again) has to restart it.</summary>
    public bool CanResume { get; set; }

    /// <summary>Human-readable: what it is doing, or why it is not doing anything.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The work was requested through <c>Hephaestus.CraftItem</c> rather than the player's own queue.</summary>
    public bool External { get; set; }

    /// <summary>0 when nothing is being crafted.</summary>
    public uint CurrentRecipeId { get; set; }

    public int CurrentCrafted { get; set; }

    public int CurrentQuantity { get; set; }

    public int ItemsCompleted { get; set; }

    public int ItemsSkipped { get; set; }

    /// <summary>Items the queue gave up on, with the reason.</summary>
    public FlaggedItemDto[] Flagged { get; set; } = Array.Empty<FlaggedItemDto>();
}

public sealed class FlaggedItemDto
{
    public uint RecipeId { get; set; }

    public string Reason { get; set; } = string.Empty;
}
