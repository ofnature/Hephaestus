using System.Collections.Generic;

namespace Hephaestus.Features.Materials;

/// <summary>Where a required material can come from.</summary>
public enum MaterialSource
{
    Inventory,
    FreeCompanyChest,
    Retainers,

    /// <summary>The material is itself craftable: expand the recipe tree and craft it.</summary>
    SubCraft,

    /// <summary>NPC vendor. Buys only the live shortfall, never a replayed batch.</summary>
    VendorPurchase,

    /// <summary>Demeter gathers it — last: "you can always gather it if you can't buy it" (user, 2026-09-21).</summary>
    Gathering,
}

/// <summary>
/// The order material sources are tried in, by account type. Pure — the caller reads the account
/// type live (ConditionFlag.OnFreeTrial) rather than configuring it per box.
/// </summary>
public static class SourcePolicy
{
    private static readonly MaterialSource[] Paid =
    {
        MaterialSource.Inventory,
        MaterialSource.FreeCompanyChest,
        MaterialSource.Retainers,
        MaterialSource.SubCraft,
        MaterialSource.VendorPurchase,
        MaterialSource.Gathering,
    };

    // Free trial cannot join an FC, hire retainers, trade, or use the market board — so it has
    // what is on hand, what it can craft, vendors (within its 300k gil cap) and what it can gather.
    private static readonly MaterialSource[] FreeTrial =
    {
        MaterialSource.Inventory,
        MaterialSource.SubCraft,
        MaterialSource.VendorPurchase,
        MaterialSource.Gathering,
    };

    public static IReadOnlyList<MaterialSource> OrderFor(bool freeTrial) => freeTrial ? FreeTrial : Paid;
}
