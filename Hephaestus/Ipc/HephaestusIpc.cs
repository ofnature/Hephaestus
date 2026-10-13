using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Hephaestus.Ipc;

/// <summary>
/// What Hephaestus publishes for other plugins.
///
/// <para>
/// <b><c>Hephaestus.IsBusy</c></b> → <c>bool</c> — true while Hephaestus is driving the character
/// (the queue or a craft run). Same shape as <c>Theseus.IsBusy</c> (and Henchman.IsBusy /
/// AutoDuty.IsStopped), so Daedalus's AutomationBusyBridge and any orchestrator can poll it with no
/// new mechanism. Level-triggered: it reflects the truth right now rather than latching. If
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
/// <b><c>Hephaestus.Stop</c></b> — stop whatever is running after the current action. Safe to call when idle.
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

    public static readonly string[] Gates =
    {
        IsBusyGate, CraftItemGate, CraftListGate, AddToQueueGate, StartQueueGate, StopGate, GetQueueStatusJsonGate, GetVersionGate,
    };

    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<string> _getVersion;
    private readonly ICallGateProvider<uint, int, bool> _craftItem;
    private readonly ICallGateProvider<object?> _stop;
    private readonly ICallGateProvider<string> _getQueueStatusJson;
    private readonly ICallGateProvider<string, bool> _craftList;
    private readonly ICallGateProvider<string, bool> _addToQueue;
    private readonly ICallGateProvider<bool> _startQueue;

    /// <remarks>Every delegate is wrapped: an exception in ours must never surface inside another plugin.</remarks>
    public HephaestusIpc(
        IDalamudPluginInterface pluginInterface,
        Func<bool> isBusy,
        Func<uint, int, bool> craftItem,
        Action stop,
        Func<string> queueStatusJson,
        Func<string, bool> craftList,
        Func<string, bool> addToQueue,
        Func<bool> startQueue)
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
