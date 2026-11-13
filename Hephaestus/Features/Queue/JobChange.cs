using System;

namespace Hephaestus.Features.Queue;

/// <summary>What a job change needs to see each tick.</summary>
/// <param name="CurrentJob">The player's ClassJob row; 0 when it cannot be read.</param>
/// <param name="LogOpen">A crafting log is open - the game refuses a gearset change while one is.</param>
/// <param name="CanSwitch">Out of the crafting stance and not occupied: a gearset change would be accepted.</param>
public readonly record struct JobChangeObservation(uint CurrentJob, bool LogOpen, bool CanSwitch);

public enum JobChangeCommand
{
    None,

    /// <summary>Close the crafting log, so the character leaves the crafting stance.</summary>
    CloseLog,

    /// <summary>Equip the saved gearset of <see cref="JobChange.Wanted"/>.</summary>
    Equip,
}

/// <summary>
/// <c>Hephaestus.ChangeJob</c>: another plugin (Selene, before it accepts a Cosmic mission for another
/// trade) asks for the character to be put on a job. The careful way the queue engine does it between
/// items, as a request of its own: close the crafting log if one is open, wait until the game would
/// accept a gearset change, equip the saved gearset, WATCH THE JOB ACTUALLY CHANGE (never trust the
/// call's answer), then let the stats settle. <see cref="Pending"/> is part of IsBusy throughout.
///
/// It ends by itself either way - on the job, or given up (the log would not close, the game never
/// became free, the gearset would not go on) - and says which in <see cref="Status"/>. The caller reads
/// the job itself once IsBusy has fallen: that is the verdict, as with every gate of ours.
/// </summary>
public sealed class JobChange
{
    /// <summary>Between two commands of ours.</summary>
    public static readonly TimeSpan CommandPacing = TimeSpan.FromMilliseconds(600);

    /// <summary>How long one equip is given to show up as a changed job (the queue's own figure).</summary>
    public static readonly TimeSpan SwitchTimeout = TimeSpan.FromSeconds(6);

    /// <summary>After the job has changed: the stats of the new gear are not there on the same frame.</summary>
    public static readonly TimeSpan AfterSwitchSettle = TimeSpan.FromSeconds(1.5);

    /// <summary>The whole request, however it is going.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    public const int MaxCloseAttempts = 5;
    public const int MaxEquipAttempts = 3;

    private DateTime _deadline;
    private DateTime _nextCommandAt;
    private DateTime _equipDeadline;
    private DateTime _settledAt;
    private int _closeAttempts;
    private int _equipAttempts;
    private bool _started;

    /// <summary>Asked, and not finished yet.</summary>
    public bool Pending { get; private set; }

    /// <summary>The job asked for; 0 when nothing was.</summary>
    public uint Wanted { get; private set; }

    /// <summary>What the change is doing, or how the last one ended.</summary>
    public string Status { get; private set; } = "Idle — no job change requested.";

    /// <param name="busy">Something of ours has the character (IsBusy): a job is not changed under running work.</param>
    /// <param name="hasGearset">A gearset is saved for the job.</param>
    /// <returns>True when the character is on the job already, or the change began. False, and nothing changed, otherwise.</returns>
    public bool Request(uint jobId, uint currentJob, bool hasGearset, bool busy)
    {
        if (jobId == 0 || currentJob == 0)
            return false;

        // Asked again for the job already being changed to: still true, nothing restarts (not a toggle).
        if (Pending)
            return jobId == Wanted;

        if (busy)
            return false;

        if (currentJob == jobId)
        {
            Status = "Already on the job asked for.";
            return true;
        }

        if (!hasGearset)
        {
            Status = "No gearset is saved for the job asked for.";
            return false;
        }

        Pending = true;
        Wanted = jobId;
        _started = false;
        _closeAttempts = 0;
        _equipAttempts = 0;
        _nextCommandAt = default;
        _equipDeadline = default;
        _settledAt = default;
        Status = "Changing job";
        return true;
    }

    /// <summary>Drop the request where it stands (the Stop button, Hephaestus.Stop).</summary>
    public void Cancel()
    {
        if (!Pending)
            return;

        Pending = false;
        Status = "Job change stopped.";
    }

    public JobChangeCommand Tick(in JobChangeObservation obs, DateTime now)
    {
        if (!Pending)
            return JobChangeCommand.None;

        if (!_started)
        {
            _started = true;
            _deadline = now + Deadline;
        }

        // On the job: wait for the stats, then it is done.
        if (obs.CurrentJob == Wanted)
        {
            if (_settledAt == default)
            {
                _settledAt = now + AfterSwitchSettle;
                Status = "On the job — letting the stats settle";
            }

            if (now >= _settledAt)
                End("On the job asked for.");
            return JobChangeCommand.None;
        }

        if (now > _deadline)
        {
            End("Gave up: the job did not change in time.");
            return JobChangeCommand.None;
        }

        if (now < _nextCommandAt)
            return JobChangeCommand.None;

        if (obs.LogOpen)
        {
            if (++_closeAttempts > MaxCloseAttempts)
            {
                End("Gave up: the crafting log would not close for a job change.");
                return JobChangeCommand.None;
            }

            _nextCommandAt = now + CommandPacing;
            Status = "Closing the crafting log to change job";
            return JobChangeCommand.CloseLog;
        }

        if (!obs.CanSwitch)
        {
            Status = "Waiting to be able to change job";
            return JobChangeCommand.None;
        }

        // An equip is on its way: give it its time before asking again.
        if (now < _equipDeadline)
            return JobChangeCommand.None;

        if (++_equipAttempts > MaxEquipAttempts)
        {
            End("Gave up: the gearset would not go on.");
            return JobChangeCommand.None;
        }

        _equipDeadline = now + SwitchTimeout;
        _nextCommandAt = now + CommandPacing;
        Status = "Equipping the job's gearset";
        return JobChangeCommand.Equip;
    }

    private void End(string status)
    {
        Pending = false;
        Status = status;
    }
}
