using System;
using System.Collections.Generic;
using System.Linq;
using Hephaestus.Features.Queue;

namespace Hephaestus.Features.Materials;

public enum SupplyKind
{
    Buy,
    Gather,
}

/// <summary>One line of a buy or gather list.</summary>
public sealed class SupplyLine
{
    public uint ItemId { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>The bag count the queue needs, as of when the list was made.</summary>
    public int Needed { get; init; }

    /// <summary>Kept current from inventory-change events — never polled.</summary>
    public int InBags { get; set; }

    /// <summary>Buy lines: what one costs at a vendor. 0 on gather lines.</summary>
    public uint UnitPrice { get; init; }

    /// <summary>The queue entries it is for, by name.</summary>
    public IReadOnlyList<string> For { get; init; } = Array.Empty<string>();

    public int Missing => Math.Max(0, Needed - InBags);

    public bool Done => InBags >= Needed;
}

/// <summary>
/// A sub-combine the queue will craft before the entries that need it. Listed because a paid account
/// may rather buy it on the market board (never automated) — bought and Refreshed, it drops off.
/// </summary>
public sealed record IntermediateLine(uint ItemId, string Name, int Quantity, IReadOnlyList<string> For);

/// <summary>Something the queue needs that neither vendors nor gathering covers.</summary>
public sealed record UnobtainableLine(uint ItemId, string Name, int Missing, IReadOnlyList<string> For);

/// <summary>
/// A buy list or a gather list: a SNAPSHOT of what the queue needs from one source, made on request
/// ("Make buy list" / "Make gather list") and remade on Refresh — by the user's choice (2026-09-21),
/// not recomputed on a timer. Only <see cref="SupplyLine.InBags"/> moves on its own, and only when
/// the inventory changes.
///
/// Both lists come from ONE material plan with vendors and gathering as sources, so they never
/// both claim the same shortfall: vendors first, gathering for what they cannot cover. Whatever the
/// bags already hold counts first, however it got there — so sub-combines bought on the market board
/// simply shrink the lists on the next Refresh (user, 2026-09-21: "follow the bags").
/// </summary>
public sealed class SupplyList
{
    public SupplyKind Kind { get; init; }

    public DateTime MadeAt { get; init; }

    public IReadOnlyList<SupplyLine> Lines { get; init; } = Array.Empty<SupplyLine>();

    /// <summary>Short of these whatever happens: not held, not craftable, not sold (or not affordable), not gatherable.</summary>
    public IReadOnlyList<UnobtainableLine> Unobtainable { get; init; } = Array.Empty<UnobtainableLine>();

    /// <summary>What the queue will craft on the way, as of this plan (and the materials above include theirs).</summary>
    public IReadOnlyList<IntermediateLine> Intermediates { get; init; } = Array.Empty<IntermediateLine>();

    /// <summary>Buy lists: gil still to spend, for what is still missing.</summary>
    public long CostOfMissing => Lines.Sum(l => (long)l.Missing * l.UnitPrice);

    public bool AllDone => Lines.All(l => l.Done);

    public void RefreshBags(Func<uint, int> held)
    {
        foreach (var line in Lines)
            line.InBags = Math.Max(0, held(line.ItemId));
    }

    /// <summary>What still has to be fetched: every unfinished line, up to its needed bag count.</summary>
    public IReadOnlyList<FetchItem> Outstanding()
        => Lines.Where(l => !l.Done).Select(l => new FetchItem(l.ItemId, l.Name, l.Needed, l.Missing)).ToList();

    /// <summary>Plan the queue once, with both sources, and split it into the two lists.</summary>
    /// <param name="entries">The queue. Entries FLAGGED for review are included: buying their materials is what the review is for.</param>
    /// <param name="recipeName">A list entry's name, for the "for" column.</param>
    public static (SupplyList Buy, SupplyList Gather) Make(
        MaterialPlanner planner, IEnumerable<CraftListItem> entries, uint currentJob,
        VendorSupply? vendors, GatherSupply? gathering, Func<uint, string> recipeName, DateTime now)
    {
        var open = entries
            .Where(e => !e.IsComplete)
            .Select(e => new CraftListItem { RecipeId = e.RecipeId, Quantity = e.Quantity, Crafted = e.Crafted })
            .ToList();

        var plan = planner.Plan(open, currentJob, vendors: vendors, gathering: gathering);

        IReadOnlyList<string> NamesOf(IEnumerable<CraftListItem> byEntry) => byEntry.Select(e => recipeName(e.RecipeId)).Distinct().ToList();

        var buy = plan.Purchases.Select(p => Line(p.ItemId, p.Name, p.Quantity, p.UnitPrice, NamesOf(p.ByEntry.Keys))).ToList();
        var gather = plan.Gathers.Select(g => Line(g.ItemId, g.Name, g.Quantity, 0, NamesOf(g.ByEntry.Keys))).ToList();
        var unobtainable = plan.Shortfalls.Select(s => new UnobtainableLine(s.ItemId, s.Name, s.Missing, NamesOf(s.ByEntry.Keys))).ToList();
        var intermediates = plan.SubCrafts
            .Select(s => new IntermediateLine(s.Recipe.ResultItemId, s.Recipe.Name, s.Crafts * Math.Max(1, s.Recipe.Yield), NamesOf(s.For)))
            .ToList();

        return (
            new SupplyList { Kind = SupplyKind.Buy, MadeAt = now, Lines = buy, Unobtainable = unobtainable, Intermediates = intermediates },
            new SupplyList { Kind = SupplyKind.Gather, MadeAt = now, Lines = gather, Unobtainable = unobtainable, Intermediates = intermediates });

        SupplyLine Line(uint itemId, string name, int quantity, uint unitPrice, IReadOnlyList<string> forEntries)
        {
            var held = planner.Held(itemId);
            return new SupplyLine { ItemId = itemId, Name = name, Needed = held + quantity, InBags = held, UnitPrice = unitPrice, For = forEntries };
        }
    }
}
