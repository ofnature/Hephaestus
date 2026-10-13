using System;
using System.Collections.Generic;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Materials;

namespace Hephaestus.Services;

/// <summary>
/// Charon's Free Company chest STORE, read over <c>Charon.Containers.GetItemJson</c> (per chest page NQ/HQ,
/// as of when the chest was last open, and whether the chest is known at all). Hephaestus DRIVES the
/// chest itself (its <see cref="ChestTrip"/>, the user's choice of 2026-09-26, the same as for the bell);
/// Charon only stores. Its reader captures any open chest window, ours included, but only every 20 s — so
/// <see cref="SupplyDesk"/> reads a page LIVE whenever it is loaded here and takes Charon's number only for
/// the pages it cannot see. Cached briefly; dropped after a trip. Fail-open.
/// </summary>
public sealed class CharonChestClient
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AvailableFor = TimeSpan.FromSeconds(5);

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, (int[] Pages, bool Known, DateTime Until)> _items = new();
    private ICallGateSubscriber<uint, string>? _getItem;
    private (bool Available, DateTime Until) _available;

    public CharonChestClient(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>Charon is loaded and publishes the container store.</summary>
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
                    available = (_getItem ??= _pluginInterface.GetIpcSubscriber<uint, string>("Charon.Containers.GetItemJson")).HasFunction;
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

    public string Status => Available ? "Charon's chest store answers" : "Charon is not loaded";

    /// <summary>NQ + HQ across the chest's pages, as Charon last saw them.</summary>
    public int Held(uint itemId)
    {
        var total = 0;
        foreach (var onPage in Read(itemId).Pages)
            total += onPage;
        return total;
    }

    /// <summary>NQ + HQ on one page (1..5), as Charon last saw it.</summary>
    public int HeldOnPage(uint itemId, int page)
        => page >= 1 && page <= ChestTrip.PageCount ? Read(itemId).Pages[page - 1] : 0;

    /// <summary>Charon has seen the chest at all; false = unknown, never "empty".</summary>
    public bool Known() => Read(0).Known;

    public void Invalidate() => _items.Clear();

    private (int[] Pages, bool Known) Read(uint itemId)
    {
        var now = DateTime.UtcNow;
        if (_items.TryGetValue(itemId, out var cached) && now < cached.Until)
            return (cached.Pages, cached.Known);

        var pages = new int[ChestTrip.PageCount];
        var known = false;
        try
        {
            var json = (_getItem ??= _pluginInterface.GetIpcSubscriber<uint, string>("Charon.Containers.GetItemJson")).InvokeFunc(itemId);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("chest", out var chest))
            {
                known = chest.TryGetProperty("known", out var k) && k.ValueKind == JsonValueKind.True;
                if (known && chest.TryGetProperty("pages", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var page in list.EnumerateArray())
                    {
                        var number = Int(page, "page"); // Charon numbers them 1..5, like the tabs
                        if (number >= 1 && number <= ChestTrip.PageCount)
                            pages[number - 1] += Int(page, "nq") + Int(page, "hq");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Hephaestus: Charon's chest store could not be read for item {0}", itemId);
        }

        _items[itemId] = (pages, known, now + CacheFor);
        return (pages, known);
    }

    private static int Int(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;
}
