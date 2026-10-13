using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Materials;

namespace Hephaestus.Services;

/// <summary>
/// Gathering, by asking Odysseus over the gates it publishes (built 2026-09-20 from the note
/// Hephaestus left in its repo, <c>HEPHAESTUS-GATHER-IPC.md</c>):
/// <c>Odysseus.Gather.CanGather(uint)</c>, <c>.Start(json)</c>, <c>.Stop()</c>, <c>.IsRunning</c>
/// (true only for runs that came through the gate — not a quest the player started meanwhile) and
/// <c>.GetStatusJson()</c>. Odysseus keeps our request as a list named "Hephaestus" in its gather
/// window, so the player can see it and finish it by hand. <c>.SetList(json)</c> — hand the list over
/// WITHOUT starting it (the Gather List tab's "Send to Odysseus", user 2026-09-21) — was asked of
/// Odysseus on 2026-09-21 and may not exist yet: <see cref="CanHandOver"/> says.
///
/// Success is never read from here: the queue re-reads the bags. The status only supplies the
/// words for a flag, and the one thing that must pause rather than flag — bags full, which Odysseus
/// reports as "bag is full" in the status and the item's note.
///
/// Fail-open: Odysseus not loaded (or too old for these gates) = not a source.
/// </summary>
public sealed class OdysseusGatherClient
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CanGatherFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AvailableFor = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, (bool Can, DateTime Until)> _canGather = new();
    private readonly Dictionary<uint, string> _notes = new();

    private ICallGateSubscriber<uint, bool>? _canGatherGate;
    private ICallGateSubscriber<string, bool>? _start;
    private ICallGateSubscriber<string, bool>? _setList;
    private ICallGateSubscriber<object?>? _stop;
    private ICallGateSubscriber<bool>? _isRunning;
    private ICallGateSubscriber<string>? _status;

    private (bool Available, DateTime Until) _available;
    private DateTime _nextPoll;

    public OdysseusGatherClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>Odysseus is loaded and publishes the gathering gates.</summary>
    public bool Available
    {
        get
        {
            var now = DateTime.UtcNow;
            if (now >= _available.Until)
            {
                bool available;
                try
                {
                    (_isRunning ??= _pluginInterface.GetIpcSubscriber<bool>("Odysseus.Gather.IsRunning")).InvokeFunc();
                    available = true;
                }
                catch
                {
                    available = false;
                }

                _available = (available, now + AvailableFor);
            }

            return _available.Available;
        }
    }

    /// <summary>Our request is still being gathered.</summary>
    public bool Active { get; private set; }

    /// <summary>It ended on something only a person can fix (bags full).</summary>
    public bool NeedsPerson { get; private set; }

    public string Status { get; private set; } = "Idle — nothing asked of Odysseus yet.";

    public IReadOnlyDictionary<uint, string> Notes => _notes;

    /// <summary>
    /// Odysseus's promise that this character can gather the item unattended. Cached a while: the planner asks
    /// per shortfall per plan, and the answer (a node exists, the gatherer's level) changes rarely.
    /// </summary>
    public bool CanGather(uint itemId)
    {
        var now = DateTime.UtcNow;
        if (_canGather.TryGetValue(itemId, out var cached) && now < cached.Until)
            return cached.Can;

        bool can;
        try
        {
            can = (_canGatherGate ??= _pluginInterface.GetIpcSubscriber<uint, bool>("Odysseus.Gather.CanGather")).InvokeFunc(itemId);
        }
        catch
        {
            can = false;
        }

        _canGather[itemId] = (can, now + CanGatherFor);
        return can;
    }

    /// <summary>Odysseus publishes <c>Odysseus.Gather.SetList</c>: a list can be handed over without starting it.</summary>
    public bool CanHandOver
    {
        get
        {
            try
            {
                return (_setList ??= _pluginInterface.GetIpcSubscriber<string, bool>("Odysseus.Gather.SetList")).HasFunction;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Put the list into Odysseus's gather window as the "Hephaestus" list; nothing starts.</summary>
    public bool HandOver(IReadOnlyList<FetchItem> items)
    {
        try
        {
            var done = (_setList ??= _pluginInterface.GetIpcSubscriber<string, bool>("Odysseus.Gather.SetList")).InvokeFunc(Request(items));
            Status = done ? $"Handed {items.Count} item(s) to Odysseus — run the \"Hephaestus\" list from its gather window." : "Odysseus refused the list — see /xllog";
            return done;
        }
        catch (Exception ex)
        {
            Status = $"Could not hand the list to Odysseus ({ex.GetType().Name})";
            return false;
        }
    }

    /// <returns>True when Odysseus began gathering (all or nothing: one item it cannot gather refuses the lot).</returns>
    public bool Start(IReadOnlyList<FetchItem> items)
    {
        _notes.Clear();
        NeedsPerson = false;

        try
        {
            var started = (_start ??= _pluginInterface.GetIpcSubscriber<string, bool>("Odysseus.Gather.Start"))
                .InvokeFunc(Request(items));
            Active = started;
            Status = started ? $"Odysseus is gathering {items.Count} item(s)" : "Odysseus refused the request — see /xllog";
            _nextPoll = DateTime.UtcNow + PollEvery;
            return started;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: asking Odysseus to gather failed");
            Status = $"Could not reach Odysseus ({ex.GetType().Name})";
            Active = false;
            return false;
        }
    }

    /// <summary>The request both gates take: each item up to a bag count; stay where the gathering ends.</summary>
    private static string Request(IReadOnlyList<FetchItem> items)
        => JsonSerializer.Serialize(new
        {
            requestedBy = "Hephaestus",
            returnHome = false,
            items = items.Select(i => new { itemId = i.ItemId, targetCount = i.Target }).ToArray(),
        });

    public void Stop()
    {
        if (!Active)
            return;

        try
        {
            (_stop ??= _pluginInterface.GetIpcSubscriber<object?>("Odysseus.Gather.Stop")).InvokeAction();
        }
        catch
        {
            // Odysseus gone: nothing is running any more either way.
        }

        Active = false;
        Status = "Stopped.";
    }

    /// <summary>Follow the run: once a second while it lasts, and once more when it ends, for the notes.</summary>
    public void Tick(DateTime now)
    {
        if (!Active || now < _nextPoll)
            return;

        _nextPoll = now + PollEvery;
        bool running;
        try
        {
            running = (_isRunning ??= _pluginInterface.GetIpcSubscriber<bool>("Odysseus.Gather.IsRunning")).InvokeFunc();
        }
        catch
        {
            running = false; // Odysseus unloaded mid-run: the gate is gone, so is the run
        }

        ReadStatus();
        if (!running)
            Active = false;
    }

    private void ReadStatus()
    {
        try
        {
            var json = (_status ??= _pluginInterface.GetIpcSubscriber<string>("Odysseus.Gather.GetStatusJson")).InvokeFunc();
            var dto = JsonSerializer.Deserialize<StatusDto>(json, Json);
            if (dto == null)
                return;

            if (dto.Status is { Length: > 0 } status)
                Status = status;

            foreach (var outcome in dto.Outcomes ?? new List<OutcomeDto>())
            {
                if (!outcome.Reached && outcome.Note is { Length: > 0 } note)
                    _notes[outcome.ItemId] = note;
            }

            // Odysseus's wording, per its note: the runner ends the run with "bag is full".
            NeedsPerson = (dto.Status ?? string.Empty).Contains("full", StringComparison.OrdinalIgnoreCase)
                          || _notes.Values.Any(n => n.Contains("full", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // An unreadable status costs the words on a flag, nothing else: the bags are the verdict.
        }
    }

    private sealed class StatusDto
    {
        public string? State { get; set; }

        public string? Status { get; set; }

        public List<OutcomeDto>? Outcomes { get; set; }
    }

    private sealed class OutcomeDto
    {
        public uint ItemId { get; set; }

        public bool Reached { get; set; }

        public string? Note { get; set; }
    }
}
