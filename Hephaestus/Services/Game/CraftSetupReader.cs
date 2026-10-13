using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Hephaestus.Features.Crafting;
using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using GameAction = Lumina.Excel.Sheets.Action;
using Item = Lumina.Excel.Sheets.Item;
using Recipe = Lumina.Excel.Sheets.Recipe;

namespace Hephaestus.Services.Game;

/// <summary>
/// Works out everything that is fixed for one craft — the simulator's parameters, the recipe's
/// condition set, and the question to put to Raphael — from the recipe sheets and the crafter's
/// live stats (food and medicine included, since the game's own attribute values already are).
/// </summary>
public sealed unsafe class CraftSetupReader
{
    // PlayerState.Attributes indices (BaseParam rows).
    private const int CraftsmanshipAttribute = 70;
    private const int ControlAttribute = 71;

    /// <summary>Crafter's Delineation — every specialist action spends one.</summary>
    private const uint DelineationItemId = 28724;

    /// <summary>Recipe.CraftType 0..7 is Carpenter..Culinarian; their ClassJob rows are 8..15.</summary>
    private const uint FirstCraftingJob = 8;

    private readonly IDataManager _data;
    private readonly IObjectTable _objects;
    private readonly GameActionMap _actions;
    private readonly HephaestusConfig _config;

    public CraftSetupReader(IDataManager data, IObjectTable objects, GameActionMap actions, HephaestusConfig config)
    {
        _data = data;
        _objects = objects;
        _actions = actions;
        _config = config;
    }

    public CraftSetup? Read(SynthesisSnapshot snapshot)
    {
        try
        {
            var player = _objects.LocalPlayer;
            var playerState = PlayerState.Instance();
            if (player == null || playerState == null)
                return null;
            if (_data.GetExcelSheet<Recipe>().GetRowOrDefault(snapshot.RecipeId) is not { } recipe)
                return null;
            if (recipe.RecipeLevelTable.ValueNullable is not { } table)
                return null;

            var job = player.ClassJob.RowId;
            var level = player.Level;
            var craftsmanship = playerState->Attributes[CraftsmanshipAttribute];
            var control = playerState->Attributes[ControlAttribute];
            if (craftsmanship <= 0 || control <= 0 || snapshot.MaxProgress <= 0)
                return null;

            var conditionsFlag = table.ConditionsFlag;
            var expert = ConditionOdds.IsExpert(conditionsFlag);
            var settings = _config.Recipes.TryGetValue(snapshot.RecipeId, out var saved) ? saved : new RecipeSettings();

            // Specialist actions each burn a delineation, so they are opt-in per recipe AND need one on hand.
            var canSpecialist = CanUseSpecialistActions(playerState, job);
            var heartAndSoul = canSpecialist && settings.AllowHeartAndSoul;
            var quickInnovation = canSpecialist && settings.AllowQuickInnovation;
            var manipulation = ManipulationUnlocked(job);

            var allowed = ActionMask.All;
            if (!manipulation)
                allowed = ActionMask.Without(allowed, CraftAction.Manipulation);
            if (!heartAndSoul)
                allowed = ActionMask.Without(allowed, CraftAction.HeartAndSoul);
            if (!quickInnovation)
                allowed = ActionMask.Without(allowed, CraftAction.QuickInnovation);
            // Trained Eye: only on recipes 10+ levels below the crafter, never on expert ones.
            if (expert || level < table.ClassJobLevel + 10)
                allowed = ActionMask.Without(allowed, CraftAction.TrainedEye);

            var baseProgress = CraftParameters.ComputeBaseProgress(craftsmanship, level, table.ClassJobLevel, table.ProgressDivider, table.ProgressModifier);
            var baseQuality = CraftParameters.ComputeBaseQuality(control, level, table.ClassJobLevel, table.QualityDivider, table.QualityModifier);

            var parameters = new CraftParameters(
                snapshot.MaxCp, snapshot.MaxDurability, snapshot.MaxProgress, snapshot.MaxQuality,
                baseProgress, baseQuality, level, allowed);

            var request = new BaselineRequest(
                RecipeLevel: (int)table.RowId,
                MaxProgress: snapshot.MaxProgress,
                MaxQuality: snapshot.MaxQuality,
                MaxDurability: snapshot.MaxDurability,
                Expert: expert,
                JobLevel: level,
                BaseProgress: baseProgress,
                BaseQuality: baseQuality,
                Craftsmanship: craftsmanship,
                Control: control,
                MaxCp: snapshot.MaxCp,
                InitialQuality: snapshot.StepIndex <= 1 ? snapshot.Quality : 0, // at step 1, quality IS what the HQ materials gave
                Manipulation: manipulation,
                HeartAndSoul: heartAndSoul,
                QuickInnovation: quickInnovation,
                BackloadProgress: settings.BackloadProgress);

            return new CraftSetup(parameters, conditionsFlag, request);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The Raphael request the FIRST craft of a recipe would make, for pre-solving it before the
    /// craft: the current job's live stats without a Synthesis window. The maxima are the
    /// RecipeLevelTable's scaled by the recipe's factors (what the window will show); the starting
    /// quality is what the NQ/HQ mix the run's own allocator would assign from the bags now gives.
    /// Null when the recipe belongs to another job — its stats cannot be read from here — or when
    /// anything cannot be read. A request that does not match the live one is not wrong, only wasted.
    /// </summary>
    public BaselineRequest? RequestFor(uint recipeId)
    {
        try
        {
            var player = _objects.LocalPlayer;
            var playerState = PlayerState.Instance();
            if (player == null || playerState == null)
                return null;
            if (_data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId) is not { } recipe)
                return null;
            if (recipe.RecipeLevelTable.ValueNullable is not { } table)
                return null;

            var job = player.ClassJob.RowId;
            if (recipe.CraftType.RowId + FirstCraftingJob != job)
                return null;

            var level = player.Level;
            var craftsmanship = playerState->Attributes[CraftsmanshipAttribute];
            var control = playerState->Attributes[ControlAttribute];
            var maxCp = (int)player.MaxCp;
            var maxProgress = table.Difficulty * recipe.DifficultyFactor / 100;
            var maxQuality = (int)(table.Quality * recipe.QualityFactor / 100);
            var maxDurability = table.Durability * recipe.DurabilityFactor / 100;
            if (craftsmanship <= 0 || control <= 0 || maxCp <= 0 || maxProgress <= 0)
                return null;

            var expert = ConditionOdds.IsExpert(table.ConditionsFlag);
            var settings = _config.Recipes.TryGetValue(recipeId, out var saved) ? saved : new RecipeSettings();
            var canSpecialist = CanUseSpecialistActions(playerState, job);

            return new BaselineRequest(
                RecipeLevel: (int)table.RowId,
                MaxProgress: maxProgress,
                MaxQuality: maxQuality,
                MaxDurability: maxDurability,
                Expert: expert,
                JobLevel: level,
                BaseProgress: CraftParameters.ComputeBaseProgress(craftsmanship, level, table.ClassJobLevel, table.ProgressDivider, table.ProgressModifier),
                BaseQuality: CraftParameters.ComputeBaseQuality(control, level, table.ClassJobLevel, table.QualityDivider, table.QualityModifier),
                Craftsmanship: craftsmanship,
                Control: control,
                MaxCp: maxCp,
                InitialQuality: FirstCraftStartingQuality(recipe, maxQuality),
                Manipulation: ManipulationUnlocked(job),
                HeartAndSoul: canSpecialist && settings.AllowHeartAndSoul,
                QuickInnovation: canSpecialist && settings.AllowQuickInnovation,
                BackloadProgress: settings.BackloadProgress);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The quality the first craft starts with, given the NQ/HQ split the run will assign from the bags as they are.</summary>
    private int FirstCraftStartingQuality(in Recipe recipe, int maxQuality)
    {
        var inventory = InventoryManager.Instance();
        if (recipe.MaterialQualityFactor == 0 || inventory == null)
            return 0;

        var items = _data.GetExcelSheet<Item>();
        var materials = new List<QualityMaterial>();
        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            var itemId = recipe.Ingredient[i].RowId;
            int amount = recipe.AmountIngredient[i];
            if (itemId == 0 || amount == 0 || items.GetRowOrDefault(itemId) is not { CanBeHq: true } item)
                continue;

            var nq = inventory->GetInventoryItemCount(itemId, false, false, false);
            var hq = inventory->GetInventoryItemCount(itemId, true, false, false);
            var hqUsed = IngredientAllocator.Allocate(amount, nq, hq) is { } mix ? mix.Hq : 0;
            materials.Add(new QualityMaterial(item.LevelItem.RowId, amount, hqUsed));
        }

        return StartingQuality.Compute(materials, maxQuality, recipe.MaterialQualityFactor);
    }

    /// <summary>A specialist with a delineation on hand can use the specialist actions; each one burns a delineation.</summary>
    private static bool CanUseSpecialistActions(PlayerState* playerState, uint job)
    {
        var inventory = InventoryManager.Instance();
        return playerState->IsMeisterFlagAndHasSoulStoneEquipped(job)
               && inventory != null
               && inventory->GetInventoryItemCount(DelineationItemId) > 0;
    }

    /// <summary>Manipulation is learned from a job quest, not from level — ask the game whether this job has it.</summary>
    private bool ManipulationUnlocked(uint job)
    {
        var id = _actions.IdFor(job, CraftAction.Manipulation);
        if (id == 0 || _data.GetExcelSheet<GameAction>().GetRowOrDefault(id) is not { } row)
            return false;

        var uiState = UIState.Instance();
        var link = row.UnlockLink.RowId;
        return link == 0 || (uiState != null && uiState->IsUnlockLinkUnlockedOrQuestCompleted(link));
    }
}
