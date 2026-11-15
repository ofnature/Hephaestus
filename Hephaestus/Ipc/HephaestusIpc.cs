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
/// BROKEN GEAR: no gate starts a synthesis on it. Every craft, whoever asked, passes the run's gear gate first
/// (<c>QueueSettings.GearConditionThresholdPercent</c>, 20% unless the player moved it): a request made on worn
/// gear still returns true - the run BEGAN - and halts at once without pressing Synthesize; <c>IsBusy</c> falls,
/// and the status reads <c>RunHalt = "GearGate"</c>, <c>CanResume = true</c>, with the gear and the gate in percent.
/// <b><c>Hephaestus.GetVersion</c></b> → <c>string</c>.
/// </para>
///
/// <para>
/// <b><c>Hephaestus.FinishUnderGearGate(int floorPercent)</c></b> → <c>bool</c> — THE WORK IN HAND may go on under
/// the player's gear gate, down to a floor (added 2026-11-15 for Selene: a Cosmic mission whose gear crosses the
/// gate between two items is finished, and repaired after). Called AFTER the start it is meant for
/// (<c>CraftSelected</c> / <c>CraftItem</c> / <c>CraftList</c> answered true), or when that work stands halted as
/// <c>RunHalt = "GearGate"</c> with <c>CanResume</c> - the craft run is then RESUMED under the floor, where it
/// stopped (the log is left as it is, as after <c>CraftSelected</c>). The floor that holds is
/// <c>min(the player's gate, max(floorPercent, 5))</c>: it only ever lowers the gate, and nobody goes under 5%.
/// It belongs to that work alone - it is gone the moment the work stops, however it stops, and nothing is saved.
/// True = taken (<c>RunGearFloorPercent</c> in the status says what holds). False, and nothing changed: nothing is
/// in hand (idle, or halted for another reason), or the gear is already under the floor. At the floor the run
/// halts as it does at the gate: <c>GearGate</c>, resumable once repaired. NOT seen in game when written.
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
    public const string FinishUnderGearGateGate = "Hephaestus.FinishUnderGearGate";

    public static readonly string[] Gates =
    {
        IsBusyGate, CraftItemGate, CraftSelectedGate, CraftListGate, AddToQueueGate, StartQueueGate, ChangeJobGate, StopGate,
        RequestStopAfterRunGate, FinishUnderGearGateGate, GetQueueStatusJsonGate, GetVersionGate,
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
    private readonly ICallGateProvider<int, bool> _finishUnderGearGate;

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
        Func<uint, bool> changeJob,
        Func<int, bool> finishUnderGearGate)
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

        _finishUnderGearGate = pluginInterface.GetIpcProvider<int, bool>(FinishUnderGearGateGate);
        _finishUnderGearGate.RegisterFunc(floor => Guard(() => finishUnderGearGate(floor), fallback: false));
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
        _finishUnderGearGate.UnregisterFunc();
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

    // ---- added 2026-11-12, for a caller that has to tell WHY nothing is being made (Selene) ----

    /// <summary>
    /// Why the craft run last stopped itself, by name: "None" while it runs or after it finished, else
    /// <c>GearGate</c> (a piece of equipped gear is under <see cref="GearGatePercent"/> - NO SYNTHESIS IS STARTED
    /// ON BROKEN GEAR, whoever asked), <c>OutOfMaterials</c>, <c>WouldNotStart</c>, <c>FailedRepeatedly</c>,
    /// <c>Cancelled</c>, <c>MaterialChoice</c>, <c>LogUnresponsive</c>, <c>Stopped</c>, <c>InternalError</c>.
    /// Names are only ever added. <see cref="Status"/> carries the sentence a person reads.
    /// </summary>
    public string RunHalt { get; set; } = "None";

    /// <summary>Lowest condition, in percent, across the gear the character has on right now.</summary>
    public int GearConditionPercent { get; set; } = 100;

    /// <summary>
    /// The player's gear gate: before EVERY synthesis the run checks the gear, and under this it starts nothing and
    /// halts as <c>GearGate</c>, resumable once repaired. A caller that wants to know beforehand compares the two.
    /// </summary>
    public int GearGatePercent { get; set; }

    // ---- added 2026-11-15, with Hephaestus.FinishUnderGearGate ----

    /// <summary>
    /// The floor UNDER the player's gate that the work in hand may go down to, because a caller asked
    /// (<c>Hephaestus.FinishUnderGearGate</c>); 0 = none, <see cref="GearGatePercent"/> is what holds.
    /// </summary>
    public int RunGearFloorPercent { get; set; }

    /// <summary>
    /// Whose sentence <see cref="Status"/> is: the queue's while it has the character, the craft run's while a run
    /// of its own is going or stands halted (Craft X, <c>CraftSelected</c>), else whichever spoke last.
    /// </summary>
    public static string StatusLine(bool queueActive, bool queueHasList, string queueStatus, bool runActive, bool runCanResume, string runStatus)
    {
        if (queueActive)
            return queueStatus;
        if (runActive || runCanResume)
            return runStatus;
        return queueHasList ? queueStatus : runStatus;
    }
}

public sealed class FlaggedItemDto
{
    public uint RecipeId { get; set; }

    public string Reason { get; set; } = string.Empty;
}
