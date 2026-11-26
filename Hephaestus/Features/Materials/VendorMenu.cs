using System.Collections.Generic;
using System.Numerics;

namespace Hephaestus.Features.Materials;

/// <summary>A seller of an item, and where they stand - <paramref name="Territory"/> 0 when the game data does not say.</summary>
public readonly record struct PlacedSeller(uint NpcDataId, uint Territory, Vector3 Position, uint ShopId = 0);

/// <summary>
/// The order the "Vendor Location" window lists an item's sellers in (2026-11-26): those in the zone the
/// character is in first, nearest first; then every other seller the way the game's sheets name them; those
/// nobody can place last. All of them - a window is a list. Pure.
/// </summary>
public static class VendorMenu
{
    /// <param name="sellers">Every seller of the item, in sheet order.</param>
    public static IReadOnlyList<PlacedSeller> Order(IReadOnlyList<PlacedSeller> sellers, uint currentTerritory, Vector3 player)
    {
        var here = new List<PlacedSeller>();
        var elsewhere = new List<PlacedSeller>();
        var unplaced = new List<PlacedSeller>();
        foreach (var seller in sellers)
        {
            if (seller.Territory == 0)
                unplaced.Add(seller);
            else if (seller.Territory == currentTerritory)
                here.Add(seller);
            else
                elsewhere.Add(seller);
        }

        // Stable, so two sellers standing on one spot keep their sheet order.
        here.Sort((a, b) => Vector3.DistanceSquared(player, a.Position).CompareTo(Vector3.DistanceSquared(player, b.Position)));
        here.AddRange(elsewhere);
        here.AddRange(unplaced);
        return here;
    }

    // [SUPERSEDED 2026-11-26 by Order — the right-click entry opens a window that lists every seller (the user: "can we
    // get a window like that?"); the submenu of one seller a zone it replaced was never seen in game]
    // /// <summary>A context submenu is short: more zones than this and the rest are left out.</summary>
    // public const int MaxEntries = 10;
    //
    // /// <param name="sellers">Every placed seller of the item, in sheet order.</param>
    // public static IReadOnlyList<PlacedSeller> Pick(IReadOnlyList<PlacedSeller> sellers, uint currentTerritory, Vector3 player)
    // {
    //     var picked = new List<PlacedSeller>();
    //     PlacedSeller? here = null;
    //     foreach (var seller in sellers)
    //     {
    //         if (seller.Territory == currentTerritory)
    //         {
    //             if (here is not { } best || Vector3.DistanceSquared(player, seller.Position) < Vector3.DistanceSquared(player, best.Position))
    //                 here = seller;
    //         }
    //         else if (!picked.Exists(p => p.Territory == seller.Territory))
    //         {
    //             picked.Add(seller);
    //         }
    //     }
    //
    //     if (here is { } nearest)
    //         picked.Insert(0, nearest);
    //     if (picked.Count > MaxEntries)
    //         picked.RemoveRange(MaxEntries, picked.Count - MaxEntries);
    //     return picked;
    // }

    /// <summary>
    /// Is this row of a shop's list the item? A shop cuts a long name short with "...", so a row that ends
    /// that way matches the item whose name it begins.
    /// </summary>
    public static bool RowNames(string shown, string itemName)
    {
        if (shown.Length == 0 || itemName.Length == 0)
            return false;
        if (string.Equals(shown, itemName, System.StringComparison.Ordinal))
            return true;

        var stem = shown.TrimEnd('.', '…').TrimEnd();
        return stem.Length > 0 && stem.Length < shown.Length && itemName.StartsWith(stem, System.StringComparison.Ordinal);
    }
}
