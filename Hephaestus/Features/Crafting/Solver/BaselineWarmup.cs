using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Solver;

/// <summary>
/// Pre-solves Raphael baselines for the crafts a queue is about to do, so the first craft of each
/// recipe does not sit on "Solving the baseline" (up to <see cref="Driving.CraftSession.SolveTimeout"/>).
/// GBR's Vulcan does this for a whole list up front; ours is narrower on purpose: one solve at a
/// time, never while a synthesis is open (the live solve and the game come first), and only for
/// requests the caller can build exactly — a key that differs from the live request is wasted work.
///
/// Pure: the solver is a delegate (the plugin passes RaphaelRunner.SolveAsync, which caches and
/// shares identical in-flight requests, so a warm-up and the live solve for the same craft become
/// one process).
/// </summary>
public sealed class BaselineWarmup
{
    private readonly Func<BaselineRequest, Task<IReadOnlyList<CraftAction>?>> _solve;
    private readonly Func<bool> _paused;
    private readonly Queue<(BaselineRequest Request, string Name)> _pending = new();
    private readonly HashSet<string> _queued = new();
    private Task? _inFlight;
    private string _inFlightName = string.Empty;

    /// <param name="paused">True while nothing may start: a synthesis is open, or the feature is switched off.</param>
    public BaselineWarmup(Func<BaselineRequest, Task<IReadOnlyList<CraftAction>?>> solve, Func<bool> paused)
    {
        _solve = solve;
        _paused = paused;
    }

    public int Pending => _pending.Count;

    public bool Solving => _inFlight is { IsCompleted: false };

    public string Status { get; private set; } = "Idle — nothing to pre-solve.";

    /// <summary>Queue what is not already queued. The same request twice is one solve.</summary>
    /// <returns>How many were new.</returns>
    public int Enqueue(IEnumerable<(BaselineRequest Request, string Name)> requests)
    {
        var added = 0;
        foreach (var (request, name) in requests)
        {
            if (!_queued.Add(RaphaelCommand.CacheKey(request)))
                continue;

            _pending.Enqueue((request, name));
            added++;
        }

        if (added > 0 && !Solving)
            Status = $"{_pending.Count} craft(s) waiting to be pre-solved";
        return added;
    }

    /// <summary>Forget what is waiting (a queue was started afresh). A solve already running finishes on its own.</summary>
    public void Clear()
    {
        _pending.Clear();
        _queued.Clear();
        if (!Solving)
            Status = "Idle — nothing to pre-solve.";
    }

    public void Tick(DateTime now)
    {
        if (Solving)
        {
            Status = $"Pre-solving {_inFlightName}" + (_pending.Count > 0 ? $" ({_pending.Count} more waiting)" : string.Empty);
            return;
        }

        if (_inFlight != null)
        {
            _inFlight = null; // finished: the result is in the cache, or it failed and the live solve will say so
            Status = _pending.Count > 0 ? $"{_pending.Count} craft(s) waiting to be pre-solved" : "Idle — the queue's baselines are ready.";
        }

        if (_pending.Count == 0)
            return;

        if (_paused())
        {
            Status = $"Paused ({_pending.Count} waiting) — a synthesis is open, or pre-solving is off";
            return;
        }

        var (request, name) = _pending.Dequeue();
        _inFlightName = name;
        _inFlight = _solve(request);
        Status = $"Pre-solving {name}" + (_pending.Count > 0 ? $" ({_pending.Count} more waiting)" : string.Empty);
    }
}
