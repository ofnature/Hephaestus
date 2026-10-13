using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Solver;

/// <summary>
/// Solved rotations on disk, keyed by <see cref="RaphaelCommand.CacheKey"/>. A solve can take many
/// seconds; the second craft of a Craft All run — and the same recipe on every other box on this
/// machine — should cost nothing. Nothing in the key is per-character, so one file serves them all.
///
/// Several game clients share the file, so a save re-reads it first and merges: a client only ever
/// ADDS its entry, it never writes back a stale copy over someone else's.
/// </summary>
public sealed class BaselineCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _gate = new();

    public BaselineCache(string configDirectory)
    {
        _path = Path.Combine(configDirectory, "raphael-cache.json");
    }

    public IReadOnlyList<CraftAction>? Get(string key)
    {
        lock (_gate)
        {
            return Read().TryGetValue(key, out var names) ? ToActions(names) : null;
        }
    }

    public void Put(string key, IReadOnlyList<CraftAction> rotation)
    {
        lock (_gate)
        {
            var entries = Read();
            entries[key] = rotation.Select(a => a.ToString()).ToArray();

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = $"{_path}.{Environment.ProcessId}.tmp"; // per-process, so two clients never share a temp file
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    private Dictionary<string, string[]> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(_path), JsonOptions) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new(); // a cache that cannot be read is an empty cache; the next Put rewrites it
        }
    }

    /// <summary>An entry naming an action this build no longer knows is treated as a miss, not an error.</summary>
    private static IReadOnlyList<CraftAction>? ToActions(string[] names)
    {
        var actions = new List<CraftAction>(names.Length);
        foreach (var name in names)
        {
            if (!CraftActionNames.TryParse(name, out var action))
                return null;
            actions.Add(action);
        }

        return actions;
    }
}
