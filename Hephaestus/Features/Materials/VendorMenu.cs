using System.Collections.Generic;
using System.Numerics;

namespace Hephaestus.Features.Materials;

/// <summary>A seller whose place is known.</summary>
public readonly record struct PlacedSeller(uint NpcDataId, uint Territory, Vector3 Position);

/// <summary>
/// Which sellers the "Vendor Location" right-click menu offers for an item (2026-11-26): ONE A ZONE - seven
/// NPCs sell Copper Ore and a menu is not a list - the zone the character is in first, and in it the seller
/// nearest to them; in every other zone the first the game's sheets name. Pure.
/// </summary>
public static class VendorMenu
{
    /// <summary>A context submenu is short: more zones than this and the rest are left out.</summary>
    public const int MaxEntries = 10;

    /// <param name="sellers">Every placed seller of the item, in sheet order.</param>
    public static IReadOnlyList<PlacedSeller> Pick(IReadOnlyList<PlacedSeller> sellers, uint currentTerritory, Vector3 player)
    {
        var picked = new List<PlacedSeller>();
        PlacedSeller? here = null;
        foreach (var seller in sellers)
        {
            if (seller.Territory == currentTerritory)
            {
                if (here is not { } best || Vector3.DistanceSquared(player, seller.Position) < Vector3.DistanceSquared(player, best.Position))
                    here = seller;
            }
            else if (!picked.Exists(p => p.Territory == seller.Territory))
            {
                picked.Add(seller);
            }
        }

        if (here is { } nearest)
            picked.Insert(0, nearest);
        if (picked.Count > MaxEntries)
            picked.RemoveRange(MaxEntries, picked.Count - MaxEntries);
        return picked;
    }
}
