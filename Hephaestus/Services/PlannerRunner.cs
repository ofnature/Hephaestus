using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Hephaestus.Features.Crafting.Planner;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Services;

/// <summary>
/// Answers a <see cref="BaselineRequest"/> with OUR planner (<see cref="OwnPlan"/>) where
/// <see cref="RaphaelRunner"/> asks raphael-cli: on background threads inside the plugin, a share of
/// the cores at below-normal priority, a few seconds the first time a recipe is met at these stats.
/// Answers are cached on disk beside Raphael's (<see cref="BaselineCache"/>, under keys of their
/// own), and identical requests already in flight are shared rather than started twice.
/// IN TWO STAGES: the first answer goes to the craft in hand and into the cache; a deeper polish of it
/// then runs on in the background and replaces the cached rotation when it finds a better one - the
/// next craft of the recipe gets that.
///
/// Pure CPU and no game access: nothing here runs on, or waits for, the framework thread.
/// </summary>
public sealed class PlannerRunner
{
    private readonly BaselineCache _cache;
    private readonly Func<int> _threads;
    private readonly Func<BaselineRequest, int, RotationPlan> _plan;
    private readonly Func<BaselineRequest, RotationPlan, int, RotationPlan>? _deepen;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, Task<IReadOnlyList<CraftAction>?>> _inFlight = new();
    private readonly object _gate = new();

    /// <param name="threads">Threads a search may take. Several game clients may be planning at once - do not take every core.</param>
    /// <param name="plan">The planner itself; tests pass a stand-in.</param>
    /// <param name="deepen">The deeper polish of a first answer; with a stand-in planner and none given, there is no second stage.</param>
    public PlannerRunner(
        BaselineCache cache, Func<int> threads, Action<string>? log = null, Func<BaselineRequest, int, RotationPlan>? plan = null,
        Func<BaselineRequest, RotationPlan, int, RotationPlan>? deepen = null)
    {
        _cache = cache;
        _threads = threads;
        _log = log;
        _plan = plan ?? ((request, count) => OwnPlan.Solve(request, count));
        _deepen = deepen ?? (plan is null ? (request, first, count) => OwnPlan.Deepen(request, first, count) : null);
    }

    /// <summary>The deeper polish in flight, or the last one finished. Tests wait on it.</summary>
    public Task Deepening { get; private set; } = Task.CompletedTask;

    /// <summary>What the runner last did, or why it is idle.</summary>
    public string Status { get; private set; } = "Idle — no rotation asked for yet.";

    /// <returns>The rotation, or null when none that finishes the item was found.</returns>
    public Task<IReadOnlyList<CraftAction>?> SolveAsync(BaselineRequest request)
    {
        var key = OwnPlan.CacheKey(request);
        if (_cache.Get(key) is { } cached)
        {
            Status = "Rotation served from the cache.";
            return Task.FromResult<IReadOnlyList<CraftAction>?>(cached);
        }

        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var running))
                return running;

            var task = Task.Run(() => Solve(request, key));
            _inFlight[key] = task;
            task.ContinueWith(_ =>
            {
                lock (_gate)
                    _inFlight.Remove(key);
            }, TaskScheduler.Default);
            return task;
        }
    }

    private IReadOnlyList<CraftAction>? Solve(BaselineRequest request, string key)
    {
        var timer = Stopwatch.StartNew();
        Status = "Planning a rotation…";
        try
        {
            var plan = _plan(request, Math.Max(1, _threads()));
            if (!plan.Completed || plan.Actions.Length == 0)
            {
                Status = "No rotation that finishes the item was found.";
                return null;
            }

            _cache.Put(key, plan.Actions);
            Status = plan.Reached
                ? $"Planned {plan.Steps} steps to full quality in {timer.Elapsed.TotalSeconds:0.0}s."
                : $"No rotation reaches full quality on paper ({plan.Quality} of {request.MaxQuality}); planned in {timer.Elapsed.TotalSeconds:0.0}s.";
            _log?.Invoke(Status);
            if (_deepen != null)
                Deepening = Task.Run(() => Deepen(request, key, plan));
            return plan.Actions;
        }
        catch (Exception ex)
        {
            Status = $"The planner failed: {ex.Message}";
            _log?.Invoke($"rotation planner failed: {ex}");
            return null;
        }
    }

    /// <summary>The second stage. The craft in hand keeps the rotation it started with; the cache gets the better one.</summary>
    private void Deepen(BaselineRequest request, string key, RotationPlan first)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var deep = _deepen!(request, first, Math.Max(1, _threads()));
            if (!deep.Completed || deep.Actions.Length == 0 || ReferenceEquals(deep, first) || !RotationPlanner.Better(deep, first))
            {
                Status = $"A deeper look found nothing better than the {first.Steps} steps ({timer.Elapsed.TotalSeconds:0.0}s).";
                return;
            }

            _cache.Put(key, deep.Actions);
            Status = deep.Reached && !first.Reached
                ? $"A deeper look found a rotation to full quality: {deep.Steps} steps ({timer.Elapsed.TotalSeconds:0.0}s). The next craft uses it."
                : $"A deeper look found {deep.Steps} steps where the first had {first.Steps} ({timer.Elapsed.TotalSeconds:0.0}s). The next craft uses it.";
            _log?.Invoke(Status);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"rotation planner, deeper look failed: {ex}");
        }
    }
}
