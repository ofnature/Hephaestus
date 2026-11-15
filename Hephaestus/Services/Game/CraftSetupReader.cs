using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Hephaestus.Features.Crafting;
using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using GameAction = Lumina.Excel.Sheets.Action;
using Item = Lumina.Excel.Sheets.Item;
using MissionRefine = Lumina.Excel.Sheets.WKSMissionToDoEvalutionRefin;
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
    private readonly Func<bool> _bulk;

    /// <param name="bulk">The craft about to start is one of many (time decides) rather than a one-off (quality first).</param>
    public CraftSetupReader(IDataManager data, IObjectTable objects, GameActionMap actions, HephaestusConfig config, Func<bool>? bulk = null)
    {
        _data = data;
        _objects = objects;
        _actions = actions;
        _config = config;
        _bulk = bulk ?? (() => false);
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
            // Cosmic Exploration duty actions: the simulator knows them, the plugin does not read a mission's charges yet.
            allowed = ActionMask.Without(ActionMask.Without(allowed, CraftAction.StellarSteadyHand), CraftAction.MaterialMiracle);
            if (!manipulation)
                allowed = ActionMask.Without(allowed, CraftAction.Manipulation);
            if (!heartAndSoul)
                allowed = ActionMask.Without(allowed, CraftAction.HeartAndSoul);
            if (!quickInnovation)
                allowed = ActionMask.Without(allowed, CraftAction.QuickInnovation);
            // Trained Eye: only on recipes 10+ levels below the crafter, never on expert ones.
            var trainedEye = !expert && level >= table.ClassJobLevel + 10;
            if (!trainedEye)
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
                BackloadProgress: settings.BackloadProgress,
                TrainedEye: trainedEye);

            // An expert recipe goes to the expert solver, with what the item must reach and what a Cosmic mission lends.
            // So does a Cosmic mission's rated item the game does not label Expert (2026-11-15: as ordinary items they
            // waited 50 s of the mission's clock for a rotation that could not fill the bar, then were made for the
            // full bar and not for their ratings - 42% and 78% where the top rating stands at 85%).
            ExpertProblem? problem = null;
            if (MadeByExpertSolver(recipe))
            {
                var duty = snapshot.DutyAction;
                parameters = parameters with
                {
                    AllowedActions = duty is { } lent ? ActionMask.With(allowed, lent) : allowed,
                    SplendorousTools = HoldsSplendorousTool(),
                };

                var tiers = CosmicRatings(recipe, snapshot.MaxQuality);
                var target = recipe.RequiredQuality > 0 ? (int)recipe.RequiredQuality : tiers?[^1] ?? snapshot.MaxQuality;
                // A scored collectable is made against the mission's clock - a Master mission's (the Expert ones: as many
                // as fit) at its full price, any other rated item at a small one; an item with a demand is worth the
                // time it takes.
                problem = new ExpertProblem(
                    ItemName(recipe, snapshot.RecipeId), parameters, conditionsFlag,
                    snapshot.StepIndex <= 1 ? snapshot.Quality : 0, target, tiers, duty,
                    TimeValue: tiers == null ? 0 : recipe.IsExpert ? ExpertTuningBook.TimeValue : ExpertTuningBook.RatedTimeValue);
            }

            // An ordinary recipe, as the expert solver would be given it - for the craft our planner finds no rotation to
            // full quality for (CraftSession decides, once the rotation is in). Time has a price only in bulk crafting.
            ExpertProblem? tight = null;
            if (problem == null && _config.Solver.ExpertSolver && _config.Solver.Planner == PlanSource.Own)
            {
                tight = new ExpertProblem(
                    ItemName(recipe, snapshot.RecipeId), parameters with { SplendorousTools = HoldsSplendorousTool() }, conditionsFlag,
                    snapshot.StepIndex <= 1 ? snapshot.Quality : 0, snapshot.MaxQuality,
                    TimeValue: _bulk() ? ExpertTuningBook.BulkTimeValue : 0,
                    ByHqChance: recipe.CanHq); // short of the full bar it is still worth its HQ chance
            }

            return new CraftSetup(parameters, conditionsFlag, request, problem, tight);
        }
        catch
        {
            return null;
        }
    }

    private static string ItemName(in Recipe recipe, uint recipeId)
    {
        var name = recipe.ItemResult.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? $"recipe {recipeId}" : name;
    }

    /// <summary>The expert solver makes it, with no rotation planned first: the game's Expert recipes and Cosmic rated items.</summary>
    private bool MadeByExpertSolver(in Recipe recipe)
        => _config.Solver.ExpertSolver && (recipe.IsExpert || RatingRow(recipe) != null);

    /// <summary>The ratings row of a Cosmic Exploration collectable, when it has one that makes sense.</summary>
    private MissionRefine? RatingRow(in Recipe recipe)
    {
        const int CosmicMetadata = 7;
        if (recipe.CollectableMetadataKey != CosmicMetadata)
            return null;
        if (_data.GetExcelSheet<MissionRefine>().GetRowOrDefault(recipe.CollectableMetadata.RowId) is not { } row)
            return null;
        if (row.Unknown0 == 0 || row.Unknown1 < row.Unknown0 || row.Unknown2 < row.Unknown1)
            return null;

        return row;
    }

    /// <summary>
    /// A Cosmic Exploration collectable's three ratings as QUALITY, lowest first; null for anything else.
    /// Recipe.CollectableMetadataKey 7 = the metadata is a WKSMissionToDoEvalutionRefin row of three percentages of
    /// the maximum quality; a rating is floor(percent x max / 1000) collectability (Artisan's formula - it matches
    /// the Synthesis window: 1315 / 1578 / 2235 for 50 / 60 / 85% of 26,300).
    /// </summary>
    private int[]? CosmicRatings(in Recipe recipe, int maxQuality)
    {
        if (RatingRow(recipe) is not { } row)
            return null;

        return new[]
        {
            row.Unknown0 * maxQuality / 1000 * 10,
            row.Unknown1 * maxQuality / 1000 * 10,
            row.Unknown2 * maxQuality / 1000 * 10,
        };
    }

    /// <summary>
    /// The main-hand tool makes Good worth x1.75 instead of x1.5: the Splendorous tools from Crystalline on and the
    /// Cosmic tools from phase 9 on. The game says so only in the item's description text; in the Item sheet they are
    /// exactly the crafting main hands of rarity 4 (pink) at item level 620 and above (checked 2026-11-11 - the
    /// replicas are rarity 4 too, at item level 1).
    /// </summary>
    private bool HoldsSplendorousTool()
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return false;

        var equipped = inventory->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped == null || equipped->Size == 0)
            return false;

        var mainHand = equipped->GetInventorySlot(0);
        if (mainHand == null || mainHand->ItemId == 0)
            return false;

        return _data.GetExcelSheet<Item>().GetRowOrDefault(mainHand->ItemId) is { Rarity: 4 } tool && tool.LevelItem.RowId >= 620;
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

            // An expert recipe or a rated mission item is not made from a plan (see Read): nothing to pre-solve.
            if (MadeByExpertSolver(recipe))
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
                BackloadProgress: settings.BackloadProgress,
                TrainedEye: !expert && level >= table.ClassJobLevel + 10);
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
