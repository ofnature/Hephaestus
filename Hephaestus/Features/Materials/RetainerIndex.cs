using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hephaestus.Features.Materials;

/// <summary>NQ and HQ of one item, as last seen in a retainer's bags.</summary>
public readonly record struct ItemCount(int Nq, int Hq)
{
    [JsonIgnore]
    public int Total => Nq + Hq;
}

/// <summary>What one retainer held the last time their bags were open.</summary>
public sealed class RetainerHolding
{
    public ulong RetainerId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Default = the retainer is known but their bags have never been seen.</summary>
    public DateTime LastSeen { get; set; }

    public Dictionary<uint, ItemCount> Items { get; set; } = new();

    [JsonIgnore]
    public bool Indexed => LastSeen != default;
}

/// <summary>
/// A character's own index of what their retainers hold. Retainer bags are only readable while
/// that retainer is open, so the index is learned PASSIVELY — every time a retainer is opened, by
/// the player or by a trip of ours, their bags are read and this is updated — and kept per
/// character (decided 2026-09-19: no AutoRetainer or Allagan Tools dependency; 2026-09-21: learn
/// passively, sweep never-indexed retainers when the queue is short). It can be stale: the planner
/// treats it as a promise good enough to plan on, and the trip judges by what it actually withdraws.
/// </summary>
public sealed class RetainerIndex
{
    public int FormatVersion { get; set; } = 1;

    /// <summary>By retainer id. Names are for people; ids are what the game's callbacks take.</summary>
    public Dictionary<ulong, RetainerHolding> Retainers { get; set; } = new();

    /// <summary>NQ + HQ of an item across every indexed retainer.</summary>
    public int Held(uint itemId)
    {
        var total = 0;
        foreach (var retainer in Retainers.Values)
        {
            if (retainer.Items.TryGetValue(itemId, out var count))
                total += count.Total;
        }

        return total;
    }

    /// <summary>Who holds an item, most of it first.</summary>
    public IReadOnlyList<(RetainerHolding Retainer, ItemCount Count)> Holders(uint itemId)
        => Retainers.Values
            .Where(r => r.Items.TryGetValue(itemId, out var c) && c.Total > 0)
            .Select(r => (r, r.Items[itemId]))
            .OrderByDescending(h => h.Item2.Total)
            .ToList();

    /// <summary>The roster: a retainer exists under this name. Their bags stay unknown until <see cref="Learn"/>.</summary>
    public void Know(ulong retainerId, string name)
    {
        if (retainerId == 0)
            return;

        if (!Retainers.TryGetValue(retainerId, out var holding))
            Retainers[retainerId] = holding = new RetainerHolding { RetainerId = retainerId };
        if (name.Length > 0)
            holding.Name = name;
    }

    /// <summary>Their bags, as seen right now: replaces what was known of this retainer entirely.</summary>
    public void Learn(ulong retainerId, string name, IReadOnlyDictionary<uint, ItemCount> items, DateTime now)
    {
        if (retainerId == 0)
            return;

        Know(retainerId, name);
        var holding = Retainers[retainerId];
        holding.LastSeen = now;
        holding.Items = items.Where(i => i.Value.Total > 0).ToDictionary(i => i.Key, i => i.Value);
    }

    /// <summary>Retainers the roster no longer lists (dismissed) are dropped, so their stock is never counted on.</summary>
    public void Prune(IReadOnlyCollection<ulong> currentRetainerIds)
    {
        foreach (var id in Retainers.Keys.Where(id => !currentRetainerIds.Contains(id)).ToList())
            Retainers.Remove(id);
    }

    /// <summary>Known but never opened: the ones a sweep would visit.</summary>
    public IReadOnlyList<RetainerHolding> NeverIndexed() => Retainers.Values.Where(r => !r.Indexed).ToList();

    /// <summary>A withdrawal just happened: keep the index honest without waiting for the next read.</summary>
    public void Withdrew(ulong retainerId, uint itemId, int nq, int hq)
    {
        if (!Retainers.TryGetValue(retainerId, out var holding) || !holding.Items.TryGetValue(itemId, out var count))
            return;

        var left = new ItemCount(Math.Max(0, count.Nq - nq), Math.Max(0, count.Hq - hq));
        if (left.Total == 0)
            holding.Items.Remove(itemId);
        else
            holding.Items[itemId] = left;
    }
}
