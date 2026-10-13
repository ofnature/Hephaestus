using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hephaestus.Features.Queue;

/// <summary>One line of a craft list: a recipe, how many to make, how many are done.</summary>
public sealed class CraftListItem
{
    /// <summary>Recipe sheet row — not the item id, since one item can have a recipe per job.</summary>
    public uint RecipeId { get; set; }

    public int Quantity { get; set; } = 1;

    public int Crafted { get; set; }

    /// <summary>
    /// Set when the queue gave up on this item (out of materials, no gearset, failed repeatedly...).
    /// A flagged item is left alone until the player clears the flag — it is a request for review,
    /// not something to retry in a loop.
    /// </summary>
    public string? SkipReason { get; set; }

    [JsonIgnore]
    public bool IsComplete => Crafted >= Quantity;

    /// <summary>Still to be made, and not flagged.</summary>
    [JsonIgnore]
    public bool IsRunnable => !IsComplete && SkipReason == null;
}

/// <summary>
/// A character's craft queue: items made in order, with progress. Progress lives in the list
/// itself, so "resume" after a pause, reload or restart is just <see cref="NextPending"/>.
/// </summary>
public sealed class CraftList
{
    /// <summary>Native format version, for import/export and future migrations.</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Craft strictly in list order. Off (the default), the queue may take an item for the job it
    /// is already on ahead of its turn, to save gearset changes — but never ahead of an item that
    /// makes one of its ingredients.
    /// </summary>
    public bool KeepOrder { get; set; }

    public List<CraftListItem> Items { get; set; } = new();

    [JsonIgnore]
    public int TotalQuantity => Items.Sum(i => i.Quantity);

    [JsonIgnore]
    public int TotalCrafted => Items.Sum(i => Math.Min(i.Crafted, i.Quantity));

    /// <summary>The first item with crafts remaining, or null when the queue is finished/empty.</summary>
    public CraftListItem? NextPending() => Items.FirstOrDefault(i => !i.IsComplete);

    /// <summary>
    /// Ask for <paramref name="quantity"/> more of a recipe. Clicking "add" three times should read as
    /// one line of three, not three lines — so it tops up the LAST entry for that recipe while that
    /// entry is still open. A finished entry is history and a flagged one is waiting for review:
    /// neither is reopened, a fresh entry is appended instead.
    /// </summary>
    /// <returns>The entry that now carries the request, or null when there was nothing to add.</returns>
    public CraftListItem? Add(uint recipeId, int quantity)
    {
        if (recipeId == 0 || quantity < 1)
            return null;

        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].RecipeId != recipeId)
                continue;

            if (Items[i].IsRunnable)
            {
                Items[i].Quantity += quantity;
                return Items[i];
            }

            break; // the most recent entry is closed; older ones are older still
        }

        var item = new CraftListItem { RecipeId = recipeId, Quantity = quantity };
        Items.Add(item);
        return item;
    }

    /// <summary>Move an item one place up (-1) or down (+1). Out-of-range moves are ignored.</summary>
    public void Move(int index, int direction)
    {
        var target = index + direction;
        if (index < 0 || index >= Items.Count || target < 0 || target >= Items.Count)
            return;

        (Items[index], Items[target]) = (Items[target], Items[index]);
    }
}
