using System;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Hephaestus.Services;

/// <summary>What Charon's store says one retainer holds of an item.</summary>
public readonly record struct CharonHolding(string Retainer, int Nq, int Hq)
{
    public int Total => Nq + Hq;
}

/// <summary>
/// Charon's retainer STORE, read over the gates it built for Hephaestus (<c>Charon.Retainers.GetItemJson</c>:
/// who holds an item, NQ/HQ, how old the answer is, and the retainers nobody has opened). Charon is the
/// fleet's retainer plugin (user, 2026-09-26), but by the same user's choice Hephaestus DRIVES THE BELL
/// itself (its <see cref="Features.Materials.RetainerTrip"/>) and Charon only stores: Charon's reader
/// captures whichever retainer is open, ours included, so a trip of ours keeps its store current too.
///
/// Retainers are keyed by NAME here — Charon's choice, the game's own presentation — and the trip
/// wants ids, so the caller maps through the live roster. Answers are cached briefly (the planner asks
/// per shortfall per plan) and dropped after a trip. Fail-open: no gate = nothing held, as far as we know.
/// </summary>
public sealed class CharonRetainerClient
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AvailableFor = TimeSpan.FromSeconds(5);

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, (IReadOnlyList<CharonHolding> Holders, IReadOnlyList<string> Unknown, DateTime Until)> _items = new();
    private ICallGateSubscriber<uint, string>? _getItem;
    private (bool Available, DateTime Until) _available;

    public CharonRetainerClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>Charon is loaded and publishes the retainer store.</summary>
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
                    available = (_getItem ??= _pluginInterface.GetIpcSubscriber<uint, string>("Charon.Retainers.GetItemJson")).HasFunction;
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

    public string Status => Available ? $"Charon's store answers ({_items.Count} item(s) asked lately)" : "Charon is not loaded";

    /// <summary>NQ + HQ across every retainer Charon has seen.</summary>
    public int Held(uint itemId)
    {
        var total = 0;
        foreach (var holder in HoldersOf(itemId))
            total += holder.Total;
        return total;
    }

    /// <summary>Who holds it, most first, by retainer name.</summary>
    public IReadOnlyList<CharonHolding> HoldersOf(uint itemId) => Read(itemId).Holders;

    /// <summary>Retainers whose bags Charon has never seen, by name.</summary>
    public IReadOnlyList<string> UnknownRetainers(uint itemId) => Read(itemId).Unknown;

    /// <summary>A trip just opened retainers: what was cached is stale.</summary>
    public void Invalidate() => _items.Clear();

    private (IReadOnlyList<CharonHolding> Holders, IReadOnlyList<string> Unknown) Read(uint itemId)
    {
        var now = DateTime.UtcNow;
        if (_items.TryGetValue(itemId, out var cached) && now < cached.Until)
            return (cached.Holders, cached.Unknown);

        var holders = new List<CharonHolding>();
        var unknown = new List<string>();
        try
        {
            var json = (_getItem ??= _pluginInterface.GetIpcSubscriber<uint, string>("Charon.Retainers.GetItemJson")).InvokeFunc(itemId);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("holdings", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var holding in list.EnumerateArray())
                {
                    var name = holding.TryGetProperty("retainer", out var r) ? r.GetString() ?? string.Empty : string.Empty;
                    if (name.Length > 0)
                        holders.Add(new CharonHolding(name, Int(holding, "nq"), Int(holding, "hq")));
                }
            }

            if (doc.RootElement.TryGetProperty("unknown", out var names) && names.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in names.EnumerateArray())
                {
                    if (name.GetString() is { Length: > 0 } text)
                        unknown.Add(text);
                }
            }

            holders.Sort((a, b) => b.Total.CompareTo(a.Total));
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Hephaestus: Charon's retainer store could not be read for item {0}", itemId);
        }

        _items[itemId] = (holders, unknown, now + CacheFor);
        return (holders, unknown);
    }

    private static int Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
