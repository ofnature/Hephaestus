using System;
using System.Collections.Generic;

namespace Hephaestus.Features.Materials;

/// <summary>Where a fetch goes for its items.</summary>
public enum FetchKind
{
    /// <summary>The Free Company chest, at the estate — <see cref="ChestTrip"/>. Paid accounts only.</summary>
    ChestWithdraw,

    /// <summary>The character's own retainers, at a Summoning Bell — <see cref="RetainerTrip"/>. Paid accounts only.</summary>
    Withdraw,

    /// <summary>NPC vendors — <see cref="VendorTrip"/>. Never the market board.</summary>
    Buy,

    /// <summary>Demeter's gatherer, over its <c>Demeter.Gather.*</c> gates (Odysseus's until 2026-10-22).</summary>
    Gather,
}

/// <summary>One item to fetch: bring the bag count up to <paramref name="Target"/>.</summary>
/// <param name="Quantity">How many that is, as of the request (Target minus what was held).</param>
public sealed record FetchItem(uint ItemId, string Name, int Target, int Quantity);

/// <summary>What the queue asks the plugin to go and get before it crafts anything more.</summary>
/// <param name="Sweep">Withdraw: also open every retainer never indexed; ChestWithdraw: go and look though the store says nothing.</param>
public sealed record FetchRequest(FetchKind Kind, IReadOnlyList<FetchItem> Items, bool Sweep = false);

/// <summary>The Free Company chest as a source, from a store of what was last seen on its pages.</summary>
/// <param name="Known">The chest has been seen at all; false means unknown, never empty.</param>
public sealed record ChestSupply(Func<uint, int> Held, Func<bool> Known);

/// <summary>Something to take from the company chest before crafting — exactly the live shortfall the bags do not cover.</summary>
public sealed record PlannedChestWithdrawal(uint ItemId, string Name, int Quantity, IReadOnlyDictionary<Queue.CraftListItem, int> ByEntry);

/// <summary>The character's retainers as a source of materials, from the index (what was last seen there).</summary>
/// <param name="NeverIndexed">How many retainers exist whose bags have never been seen.</param>
public sealed record RetainerSupply(Func<uint, int> Held, Func<int> NeverIndexed);

/// <summary>Something to withdraw from retainers before crafting — exactly the live shortfall the bags do not cover.</summary>
public sealed record PlannedWithdrawal(uint ItemId, string Name, int Quantity, IReadOnlyDictionary<Queue.CraftListItem, int> ByEntry);

/// <summary>Demeter as a source of materials: what this character could gather unattended.</summary>
public sealed record GatherSupply(Func<uint, bool> CanGather);

/// <summary>Something to gather — whatever vendors could not cover, exactly the live shortfall.</summary>
public sealed record PlannedGather(uint ItemId, string Name, int Quantity, IReadOnlyDictionary<Queue.CraftListItem, int> ByEntry);

/// <summary>The outside sources the queue may use on its own right now; either may be absent.</summary>
public sealed record MaterialSources(VendorSupply? Vendors, GatherSupply? Gathering, RetainerSupply? Retainers = null, ChestSupply? Chest = null);
