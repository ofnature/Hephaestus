using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Services;

/// <summary>
/// Runs the bundled raphael-cli as a separate, low-priority process and returns its rotation.
/// Never on the framework thread, never inside the game process: a solver crash or a long solve
/// costs a baseline, not the game. Answers are cached on disk (<see cref="BaselineCache"/>), and
/// identical requests already in flight are shared rather than started twice.
/// </summary>
public sealed class RaphaelRunner
{
    private readonly string _exePath;
    private readonly BaselineCache _cache;
    private readonly Func<int> _threads;
    private readonly IPluginLog _log;
    private readonly Dictionary<string, Task<IReadOnlyList<CraftAction>?>> _inFlight = new();
    private readonly object _gate = new();

    /// <param name="pluginDirectory">Where Hephaestus.dll lives; the CLI ships beside it under raphael/.</param>
    /// <param name="threads">Solver threads. Several game clients may be solving at once — do not take every core.</param>
    public RaphaelRunner(string pluginDirectory, BaselineCache cache, Func<int> threads, IPluginLog log)
    {
        _exePath = Path.Combine(pluginDirectory, "raphael", "raphael-cli.exe");
        _cache = cache;
        _threads = threads;
        _log = log;
    }

    /// <summary>False when the CLI was not bundled with this build (or antivirus removed it).</summary>
    public bool Available => File.Exists(_exePath);

    public string Status { get; private set; } = "Idle.";

    public Task<IReadOnlyList<CraftAction>?> SolveAsync(BaselineRequest request)
    {
        var key = RaphaelCommand.CacheKey(request);
        if (_cache.Get(key) is { } cached)
        {
            Status = "Baseline served from the cache.";
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
        var arguments = RaphaelCommand.Arguments(request, Math.Max(1, _threads()));
        var timer = Stopwatch.StartNew();
        Status = "Solving…";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            process.Start();
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal; // the game comes first
            }
            catch (Exception)
            {
                // Already exited, or not permitted — priority is a courtesy, not a requirement.
            }

            // Drain stderr concurrently so a chatty solver cannot fill the pipe and deadlock.
            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            var rotation = process.ExitCode == 0 ? RaphaelCommand.ParseActions(output) : null;
            if (rotation == null)
            {
                Status = $"Raphael gave no usable rotation (exit {process.ExitCode}).";
                _log.Warning("raphael-cli exit {0}, args: {1}\nstdout: {2}\nstderr: {3}", process.ExitCode, arguments, output.Trim(), errors.Result.Trim());
                return null;
            }

            _cache.Put(key, rotation);
            Status = $"Solved {rotation.Count} steps in {timer.Elapsed.TotalSeconds:0.0}s.";
            _log.Info("raphael-cli solved {0} steps in {1:0.0}s", rotation.Count, timer.Elapsed.TotalSeconds);
            return rotation;
        }
        catch (Exception ex)
        {
            Status = $"Raphael could not be run: {ex.Message}";
            _log.Error(ex, "raphael-cli failed to run ({0})", _exePath);
            return null;
        }
    }
}
