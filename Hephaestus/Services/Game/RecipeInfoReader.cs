using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Queue;
using Item = Lumina.Excel.Sheets.Item;
using Recipe = Lumina.Excel.Sheets.Recipe;

namespace Hephaestus.Services.Game;

/// <summary>What the queue needs to know about a recipe, from the Recipe sheet.</summary>
public sealed class RecipeInfoReader
{
    /// <summary>Recipe.CraftType 0..7 is Carpenter..Culinarian; their ClassJob rows are 8..15.</summary>
    private const uint FirstCraftingJob = 8;

    private readonly IDataManager _data;
    private Dictionary<uint, List<RecipeOption>>? _byItem;

    public RecipeInfoReader(IDataManager data)
    {
        _data = data;
    }

    public RecipeInfo? Read(uint recipeId)
    {
        if (_data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId) is not { } recipe || recipe.ItemResult.RowId == 0)
            return null;

        // Materials and crystals alike: the sheet lists both as ingredients, with an amount each.
        var inputs = new List<RecipeInput>();
        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            if (recipe.Ingredient[i].RowId != 0 && recipe.AmountIngredient[i] > 0)
                inputs.Add(new RecipeInput(recipe.Ingredient[i].RowId, recipe.AmountIngredient[i]));
        }

        return new RecipeInfo(
            recipeId,
            recipe.CraftType.RowId + FirstCraftingJob,
            recipe.ItemResult.ValueNullable?.Name.ExtractText() ?? $"recipe {recipeId}",
            recipe.ItemResult.RowId,
            inputs,
            Yield: Math.Max(1, (int)recipe.AmountResult),
            JobLevel: recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0);
    }

    public string ItemName(uint itemId)
        => _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() is { Length: > 0 } name ? name : $"item {itemId}";

    /// <summary>
    /// Every recipe that makes an item (one per crafting job that can), in sheet order; empty when
    /// it is not craftable. Indexed once on first use — a context menu opens far too often to scan
    /// the whole Recipe sheet each time.
    /// </summary>
    public IReadOnlyList<RecipeOption> RecipesFor(uint itemId)
    {
        if (_byItem == null)
        {
            _byItem = new Dictionary<uint, List<RecipeOption>>();
            foreach (var recipe in _data.GetExcelSheet<Recipe>())
            {
                var result = recipe.ItemResult.RowId;
                if (result == 0)
                    continue;

                if (!_byItem.TryGetValue(result, out var options))
                    _byItem[result] = options = new List<RecipeOption>(1);
                options.Add(new RecipeOption(recipe.RowId, recipe.CraftType.RowId + FirstCraftingJob));
            }
        }

        return _byItem.TryGetValue(itemId, out var found) ? found : Array.Empty<RecipeOption>();
    }
}
