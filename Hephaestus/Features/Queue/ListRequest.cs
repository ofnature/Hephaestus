using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Hephaestus.Features.Queue;

/// <summary>
/// A craft list described by another plugin, as JSON — the wire format of the
/// <c>Hephaestus.CraftList</c> and <c>Hephaestus.AddToQueue</c> gates:
///
/// <code>
/// { "keepOrder": true,
///   "items": [ { "recipeId": 35499, "quantity": 12 },
///              { "itemId": 5057 } ] }
/// </code>
///
/// Each entry names a <c>recipeId</c> or, for callers that only know what they want and not how it
/// is made, an <c>itemId</c> (the recipe is then chosen for the character's current job where there
/// is one — see <see cref="RecipeChoice"/>). <c>quantity</c> defaults to 1; <c>keepOrder</c>
/// defaults to TRUE, unlike the player's own queue: a caller that lists sub-crafts first means it.
///
/// ALL OR NOTHING. One bad entry rejects the whole request, with the reason. Crafting "the parts
/// of your list I understood" would leave the caller guessing which items exist afterwards; a flat
/// refusal it can log and fix. EXTEND-ONLY: unknown fields are ignored, known ones never change meaning.
/// </summary>
public static class ListRequest
{
    public readonly record struct Result(CraftList? List, string? Error)
    {
        public bool Ok => List != null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <param name="recipeExists">Whether the game knows a recipe id.</param>
    /// <param name="recipeForItem">The recipe to use for an item id; null when the item cannot be crafted.</param>
    public static Result Parse(string? json, Func<uint, bool> recipeExists, Func<uint, uint?> recipeForItem)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Fail("the request is empty");

        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<Dto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Fail($"the request is not valid JSON ({ex.Message})");
        }

        if (dto?.Items == null || dto.Items.Count == 0)
            return Fail("the request has no items");

        var list = new CraftList { KeepOrder = dto.KeepOrder ?? true };
        for (var i = 0; i < dto.Items.Count; i++)
        {
            var entry = dto.Items[i];
            if (entry == null)
                return Fail($"item {i} is null");

            var quantity = entry.Quantity ?? 1;
            if (quantity < 1)
                return Fail($"item {i} has quantity {quantity}; it must be at least 1");

            uint recipeId;
            if (entry.RecipeId is { } byRecipe and not 0)
            {
                if (!recipeExists(byRecipe))
                    return Fail($"item {i}: the game has no recipe {byRecipe}");
                recipeId = byRecipe;
            }
            else if (entry.ItemId is { } byItem and not 0)
            {
                if (recipeForItem(byItem) is not { } resolved)
                    return Fail($"item {i}: item {byItem} cannot be crafted");
                recipeId = resolved;
            }
            else
            {
                return Fail($"item {i} names neither a recipeId nor an itemId");
            }

            list.Add(recipeId, quantity);
        }

        return new Result(list, null);
    }

    private static Result Fail(string error) => new(null, error);

    private sealed class Dto
    {
        public bool? KeepOrder { get; set; }

        public List<ItemDto?>? Items { get; set; }
    }

    private sealed class ItemDto
    {
        public uint? RecipeId { get; set; }

        public uint? ItemId { get; set; }

        public int? Quantity { get; set; }
    }
}
