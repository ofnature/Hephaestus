using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Hephaestus.Features.Crafting;
using Hephaestus.Services.Game;
using Hephaestus.Windows.Components;

namespace Hephaestus.Windows;

/// <summary>
/// Sits beside the game's crafting log, the way Artisan's crafting window does: per-recipe options
/// for whatever recipe is selected, and the Craft All / Craft X control that hands the run to
/// <see cref="CraftRun"/>. The plugin opens it while the log is visible or a run is active, so it
/// has no close button of its own.
/// </summary>
public sealed class CraftingLogWindow : Window
{
    private static readonly string[] OverrideLabels = { "Follow global", "Assisted (you press)", "Auto (plugin presses)" };

    private readonly HephaestusConfig _config;
    private readonly Action _saveConfig;
    private readonly AccountReader _account;
    private readonly CraftingLogGame _game;
    private readonly ConsumableReader _consumables;
    private readonly CraftRun _run;
    private readonly CraftRunLauncher _launcher;
    private readonly Action<uint, int> _addToQueue;
    private readonly Func<CraftRunObservation> _observation;
    private readonly Func<uint, (int Crafts, int Hq)?> _outcomeFor;

    public CraftingLogWindow(
        HephaestusConfig config,
        Action saveConfig,
        AccountReader account,
        CraftingLogGame game,
        ConsumableReader consumables,
        CraftRun run,
        CraftRunLauncher launcher,
        Action<uint, int> addToQueue,
        Func<CraftRunObservation> observation,
        Func<uint, (int Crafts, int Hq)?> outcomeFor)
        : base("Hephaestus — Crafting##HephaestusCraftingLog",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        _config = config;
        _saveConfig = saveConfig;
        _account = account;
        _game = game;
        _consumables = consumables;
        _run = run;
        _launcher = launcher;
        _addToQueue = addToQueue;
        _observation = observation;
        _outcomeFor = outcomeFor;

        ShowCloseButton = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    public override void PreDraw()
    {
        // Appearing only: it opens docked to the log's top-right corner, then stays where you drag it.
        if (_game.LogTopRight is { } anchor)
            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(anchor + new Vector2(4f, 0f), ImGuiCond.Appearing);
    }

    public override void Draw()
    {
        var observation = _observation();

        HephaestusHeader.DrawCompact("Crafting");

        // While a run is going (or halted with crafts left) the window is about THAT recipe, even
        // though the log itself is hidden during a synthesis.
        var recipeId = _run.Active || _run.CanResume ? _run.RecipeId : observation.SelectedRecipeId;
        if (recipeId == 0)
        {
            ImGui.TextColored(HephaestusTheme.TextSecondary, "Select a recipe in the crafting log.");
            return;
        }

        ImGui.SetWindowFontScale(1.1f);
        HephaestusTheme.Text(_game.RecipeName(recipeId), HephaestusTheme.AccentGold);
        ImGui.SetWindowFontScale(1f);

        DrawMode();
        if (!_run.Active)
            DrawMissing();
        DrawConsumables(recipeId);
        DrawSolverOptions(recipeId);
        DrawRun(recipeId, observation);
        DrawOutcome(recipeId, observation.CosmicLog || (_run.Active && _run.InCosmicLog));
    }

    /// <param name="rated">A Cosmic mission's item: it has ratings, not quality grades - the tally counts its top rating.</param>
    private void DrawOutcome(uint recipeId, bool rated)
    {
        if (_outcomeFor(recipeId) is not { } outcome || outcome.Crafts == 0)
            return;

        var pct = (int)(100L * outcome.Hq / outcome.Crafts);
        HephaestusTheme.SectionLabel(rated ? "Top rating" : "HQ rate");
        HephaestusTheme.Text(
            rated ? $"{pct}% at the top rating — {outcome.Hq} of {outcome.Crafts} crafts" : $"{pct}% HQ — {outcome.Hq} of {outcome.Crafts} crafts",
            HephaestusTheme.TextSecondary);
    }

    private void DrawMode()
    {
        var contentId = _account.LocalContentId;
        if (contentId == 0)
            return;

        // Index 0 = follow global; 1.. = the ExecutionMode value + 1.
        var current = _config.Characters.TryGetValue(contentId, out var existing) ? existing.ModeOverride : null;
        var index = current is { } mode ? (int)mode + 1 : 0;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Mode", ref index, OverrideLabels, OverrideLabels.Length))
        {
            _config.CharacterFor(contentId).ModeOverride = index == 0 ? null : (ExecutionMode)(index - 1);
            _saveConfig();
        }
        HephaestusTheme.HelpMarker($"This character only. Global default: {_config.DefaultMode}.");
    }

    private void DrawMissing()
    {
        var missing = _game.MissingIngredients();
        if (missing.Count == 0)
            return;

        ImGui.TextColored(HephaestusTheme.StatusYellow, "Missing materials:");
        foreach (var item in missing)
            ImGui.TextColored(HephaestusTheme.StatusYellow, $"  {item.Name}  {item.Have} / {item.Need}");
    }

    private void DrawConsumables(uint recipeId)
    {
        HephaestusTheme.SectionLabel("Consumables");
        _consumables.Refresh(DateTime.UtcNow);

        var settings = SettingsFor(recipeId);

        if (ConsumableCombo("Food", _consumables.Food, settings.FoodItemId, settings.FoodHq, out var food))
        {
            settings.FoodItemId = food.ItemId;
            settings.FoodHq = food.Hq;
            Save(recipeId, settings);
        }

        if (ConsumableCombo("Medicine", _consumables.Medicine, settings.MedicineItemId, settings.MedicineHq, out var medicine))
        {
            settings.MedicineItemId = medicine.ItemId;
            settings.MedicineHq = medicine.Hq;
            Save(recipeId, settings);
        }
    }

    private void DrawSolverOptions(uint recipeId)
    {
        HephaestusTheme.SectionLabel("Raphael baseline");

        var settings = SettingsFor(recipeId);

        var backload = settings.BackloadProgress;
        if (ImGui.Checkbox("Backload progress", ref backload))
        {
            settings.BackloadProgress = backload;
            Save(recipeId, settings);
        }
        HephaestusTheme.HelpMarker("Only use Progress actions at the end of the rotation.");

        var heartAndSoul = settings.AllowHeartAndSoul;
        if (ImGui.Checkbox("Allow Heart and Soul", ref heartAndSoul))
        {
            settings.AllowHeartAndSoul = heartAndSoul;
            Save(recipeId, settings);
        }

        var quickInnovation = settings.AllowQuickInnovation;
        if (ImGui.Checkbox("Allow Quick Innovation", ref quickInnovation))
        {
            settings.AllowQuickInnovation = quickInnovation;
            Save(recipeId, settings);
        }

        ImGui.TextColored(HephaestusTheme.StatusGrey, _config.Solver.Enabled
            ? "These options take effect on the next craft."
            : "The solver is switched off in Settings.");
    }

    private void DrawRun(uint recipeId, CraftRunObservation observation)
    {
        HephaestusTheme.SectionLabel("Craft");

        if (_run.Active)
        {
            ImGui.ProgressBar((float)_run.Completed / _run.Requested, new Vector2(260f, 0f), $"{_run.Completed} / {_run.Requested}");
            ImGui.TextColored(HephaestusTheme.StatusGreen, _run.Status);
            if (Buttons.Action("Stop"))
                _run.Halt("stopped by you");

            // The gentle one: the craft in hand is finished and counted, and no other is started.
            ImGui.SameLine();
            if (_run.StoppingAfterCraft)
            {
                ImGui.TextColored(HephaestusTheme.StatusYellow, "stopping after this craft");
            }
            else
            {
                if (ImGui.Button("Stop after this craft"))
                    _run.StopAfterCraft("stopped by you after the craft in hand");
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Finishes the craft in progress, then pauses the run. Resume picks it up again.");
            }

            return;
        }

        ImGui.TextColored(_run.CanResume ? HephaestusTheme.StatusYellow : HephaestusTheme.TextSecondary, _run.Status);

        if (_run.CanResume)
        {
            if (Buttons.Action($"Resume ({_run.Requested - _run.Completed} left)"))
                _launcher.Resume();
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
                _run.Clear();
            return;
        }

        // Same count and same rules as the button inside the crafting log — they share the launcher.
        var count = _launcher.Count;
        ImGui.SetNextItemWidth(110f);
        if (ImGui.InputInt("##craftCount", ref count))
            _launcher.Count = Math.Clamp(count, 0, 9999);
        HephaestusTheme.HelpMarker("How many to craft. 0 = all. Craft never goes past the max your materials and crystals make; adding to the queue is not limited by it.");

        ImGui.SameLine();
        if (Buttons.Action(_launcher.Label(observation.Craftable), _launcher.CanStart(observation.SelectedRecipeId, observation.Craftable)))
            _launcher.Start(observation.SelectedRecipeId, observation.Craftable);

        // Queueing does not need the materials to be on hand — that is what a queue is for.
        ImGui.SameLine();
        var queued = QueueQuantity(_launcher.Count);
        ImGui.BeginDisabled(observation.SelectedRecipeId == 0 || observation.CosmicLog); // a mission's recipe only exists inside its mission
        if (ImGui.Button($"Add {queued} to queue"))
            _addToQueue(observation.SelectedRecipeId, queued);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(observation.CosmicLog
                ? "A Cosmic mission's recipe cannot be queued - use Craft All."
                : "Adds this recipe to your Hephaestus queue instead of crafting it now.\nUses the count on the left; 0 adds one.");
    }

    /// <summary>The count box means \"all I can make\" at 0 for Craft All; for the queue, 0 simply adds one.</summary>
    internal static int QueueQuantity(int count) => Math.Max(1, count);

    private bool ConsumableCombo(string label, IReadOnlyList<Consumable> onHand, uint selectedId, bool selectedHq, out Consumable picked)
    {
        picked = default;
        var changed = false;

        var current = onHand.FirstOrDefault(c => c.ItemId == selectedId && c.Hq == selectedHq);
        var preview = selectedId == 0 ? "None"
            : current.ItemId != 0 ? current.Label
            : "Selected item is not in your bags";

        ImGui.SetNextItemWidth(260f);
        if (!ImGui.BeginCombo(label, preview))
            return false;

        if (ImGui.Selectable("None", selectedId == 0))
        {
            picked = default;
            changed = true;
        }

        foreach (var consumable in onHand)
        {
            if (ImGui.Selectable(consumable.Label, consumable.ItemId == selectedId && consumable.Hq == selectedHq))
            {
                picked = consumable;
                changed = true;
            }
        }

        ImGui.EndCombo();
        return changed;
    }

    /// <summary>The saved settings for a recipe, or fresh defaults — nothing is stored until something changes.</summary>
    private RecipeSettings SettingsFor(uint recipeId)
        => _config.Recipes.TryGetValue(recipeId, out var settings) ? settings : new RecipeSettings();

    private void Save(uint recipeId, RecipeSettings settings)
    {
        _config.Recipes[recipeId] = settings;
        _saveConfig();
    }
}
