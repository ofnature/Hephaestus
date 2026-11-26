using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hephaestus.Features.Materials;

/// <summary>
/// What a special merchant asks for (2026-11-26, the Vendor Location window's "special merchants": seals, scrips,
/// tomestones, an item for an item). Pure: the two rules the game's sheets leave to its code.
/// </summary>
public static class SpecialCost
{
    /// <summary><c>SpecialShop.UseCurrencyType</c>: the cost's item id is a SLOT of this kind when it is under 8.</summary>
    public const byte ScripCurrency = 16;
    public const byte GilCurrency = 8;
    public const byte TomestoneCurrency = 4;

    /// <summary>Item id of gil.</summary>
    public const uint Gil = 1;

    /// <summary>
    /// The slots of a scrip shop, as items: Allagan Tomestone of Poetics, the purple and the orange crafters' and
    /// gatherers' scrips. The game holds this in code, so it is a table here and CHANGES WITH AN EXPANSION -
    /// read from Item Vendor Location's own (game version 2026.11.03).
    /// </summary>
    private static readonly Dictionary<uint, uint> ScripSlots = new() { [1] = 28, [2] = 33913, [4] = 33914, [6] = 41784, [7] = 41785 };

    /// <summary>Two shops (Quinnana's) are marked as tomestone shops in the sheet and sell for scrips.</summary>
    private static bool SellsForScripsAnyway(uint shopId) => shopId is 1770637 or 1770638;

    /// <summary>
    /// The item a special shop's cost names. Ids of 8 and above are the item itself; below that they are a slot
    /// of the shop's currency kind. 0 = nothing we can name.
    /// </summary>
    /// <param name="tomestoneItem">The item of a tomestone slot (TomestonesItem sheet); 0 when unknown.</param>
    public static uint CurrencyItem(uint costItemId, byte useCurrencyType, uint shopId, Func<uint, uint> tomestoneItem)
    {
        if (costItemId is 0 or >= 8)
            return costItemId;

        if (SellsForScripsAnyway(shopId))
            useCurrencyType = ScripCurrency;

        return useCurrencyType switch
        {
            ScripCurrency => ScripSlots.TryGetValue(costItemId, out var scrip) ? scrip : 0,
            GilCurrency => Gil,
            TomestoneCurrency => tomestoneItem(costItemId),
            _ => costItemId,
        };
    }

    /// <summary>"600 × Storm Seal", "1 × Duck Broth + 2 × Fire Shard", "4,620 gil".</summary>
    public static string Text(IReadOnlyList<(uint Count, uint ItemId, string Name)> costs)
    {
        var text = new StringBuilder();
        foreach (var (count, itemId, name) in costs)
        {
            if (count == 0 || name.Length == 0)
                continue;
            if (text.Length > 0)
                text.Append(" + ");
            text.Append(itemId == Gil
                ? string.Create(CultureInfo.InvariantCulture, $"{count:N0} gil")
                : string.Create(CultureInfo.InvariantCulture, $"{count:N0} × {name}"));
        }

        return text.ToString();
    }
}
