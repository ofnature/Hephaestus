using System;

namespace Hephaestus.Features.Queue;

/// <summary>What a stop-after-run needs to see each tick.</summary>
/// <param name="Working">Anything of ours still has the character: the queue, a craft run, a trip, a synthesis Auto is pressing.</param>
/// <param name="LogOpen">The crafting log is open.</param>
/// <param name="CanMove">Out of the crafting stance and not occupied: a gearset change (or a teleport) would be accepted.</param>
public readonly record struct StopObservation(bool Working, bool LogOpen, bool CanMove);

public enum StopCommand
{
    None,

    /// <summary>Start nothing new: the craft run stops after its synthesis, the queue after it, trips at once.</summary>
    StopWork,

    /// <summary>Close the crafting log, so the character leaves the crafting stance.</summary>
    CloseLog,
}

/// <summary>
/// <c>Hephaestus.RequestStopAfterRun</c>: another plugin (Argus, before it takes the character somewhere)
/// asks Hephaestus to finish what it is in and let go. The synthesis in progress is always finished —
/// a craft is never abandoned — then nothing more starts, the crafting log is closed, and the request
/// ends once the game shows the character out of the stance, as an IPC list ends
/// (<see cref="QueuePhase.Leaving"/>, same timings). <see cref="Pending"/> is part of IsBusy throughout.
///
/// Idle when asked: refused, nothing changes, so no stale request stops the next start. Asked again
/// while pending: still true, nothing changes (not a toggle). New work started once ours has ended
/// (a fresh start from the window or another plugin) cancels it; so does <see cref="Cancel"/>.
/// </summary>
public sealed class StopAfterRun
{
    private enum Step
    {
        Idle,
        Asked,
        Waiting,
        Leaving,
    }

    private Step _step;
    private DateTime _deadline;
    private DateTime _nextCommandAt;
    private DateTime _freeSince;
    private int _closeAttempts;

    /// <summary>Asked, and not finished yet: the character is still ours to hand back.</summary>
    public bool Pending => _step != Step.Idle;

    /// <summary>What the stop is doing, or why there is none.</summary>
    public string Status { get; private set; } = "Idle — no stop requested.";

    /// <param name="busy">Hephaestus has the character right now (IsBusy).</param>
    /// <returns>True when it was busy and will stop (or already is stopping); false, and nothing changed, when idle.</returns>
    public bool Request(bool busy)
    {
        if (Pending)
            return true;
        if (!busy)
            return false;

        _step = Step.Asked;
        Status = "Asked to stop after the craft in progress";
        return true;
    }

    /// <summary>A fresh start: whoever asked for the stop no longer gets it.</summary>
    public void Cancel(string why)
    {
        if (!Pending)
            return;

        _step = Step.Idle;
        Status = $"Stop cancelled — {why}";
    }

    public StopCommand Tick(in StopObservation obs, DateTime now)
    {
        switch (_step)
        {
            case Step.Asked:
                _step = Step.Waiting;
                Status = "Finishing the craft in progress, then stopping";
                return StopCommand.StopWork;

            case Step.Waiting:
                if (obs.Working)
                    return StopCommand.None;

                _step = Step.Leaving;
                _deadline = now + QueueRun.LeaveTimeout;
                _nextCommandAt = default;
                _freeSince = default;
                _closeAttempts = 0;
                Status = "Stopped — leaving the crafting stance";
                return TickLeaving(obs, now);

            case Step.Leaving:
                return TickLeaving(obs, now);

            default:
                return StopCommand.None;
        }
    }

    private StopCommand TickLeaving(in StopObservation obs, DateTime now)
    {
        // Ours had all ended, so anything working now was started afresh: that start overrides the stop.
        if (obs.Working)
        {
            Cancel("new work was started");
            return StopCommand.None;
        }

        if (!obs.LogOpen && obs.CanMove)
        {
            if (_freeSince == default)
                _freeSince = now;
            if (now - _freeSince >= QueueRun.LeaveSettle)
                Done("Stopped — the character is free to move.");
            return StopCommand.None;
        }

        _freeSince = default;
        if (now > _deadline)
        {
            Done("Stopped — the character is still in the crafting stance; it could not be left in time.");
            return StopCommand.None;
        }

        // Log away (the last synthesis is still winding down, or the tools are being put away): wait for the game.
        if (!obs.LogOpen || now < _nextCommandAt)
            return StopCommand.None;

        if (++_closeAttempts > QueueRun.MaxCloseAttempts)
        {
            Done("Stopped — the crafting log would not close.");
            return StopCommand.None;
        }

        _nextCommandAt = now + QueueRun.CommandPacing;
        Status = "Stopped — closing the crafting log, so the character is free to move";
        return StopCommand.CloseLog;
    }

    private void Done(string status)
    {
        _step = Step.Idle;
        Status = status;
    }
}
