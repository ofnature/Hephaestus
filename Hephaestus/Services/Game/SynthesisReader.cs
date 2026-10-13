using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Services.Game;

/// <summary>
/// Reads the synthesis in progress into a <see cref="SynthesisSnapshot"/>: the Synthesis window's
/// AtkValues, the player's CP, and the crafting buffs on the status list. Fail-open — anything
/// missing means "no synthesis", and the session simply waits. Indices and status ids are the ones
/// Artisan has field-verified (docs/artisan-notes.md).
/// </summary>
public sealed unsafe class SynthesisReader
{
    // Synthesis AtkValues.
    private const int Progress = 5;
    private const int MaxProgress = 6;
    private const int Durability = 7;
    private const int MaxDurability = 8;
    private const int Quality = 9;
    private const int ConditionIndex = 12;
    private const int StepIndex = 15;
    private const int MaxQuality = 17;
    private const int MinAtkValues = 26;

    // Status ids.
    private const uint InnerQuiet = 251;
    private const uint WasteNot = 252;
    private const uint WasteNot2 = 257;
    private const uint GreatStrides = 254;
    private const uint Manipulation = 1164;
    private const uint Innovation = 2189;
    private const uint MuscleMemory = 2191;
    private const uint Veneration = 2226;
    private const uint HeartAndSoul = 2665;
    private const uint Expedience = 3812;
    private const uint TrainedPerfection = 3813;

    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objects;
    private readonly ICondition _condition;

    public SynthesisReader(IGameGui gameGui, IObjectTable objects, ICondition condition)
    {
        _gameGui = gameGui;
        _objects = objects;
        _condition = condition;
    }

    /// <summary>An action is resolving, or the craft is starting/finishing — not a moment to read or act.</summary>
    public bool Transitioning => _condition[ConditionFlag.ExecutingCraftingAction];

    /// <summary>Top-right corner of the Synthesis window, for the overlay; null when it is closed.</summary>
    public System.Numerics.Vector2? WindowTopRight
    {
        get
        {
            var addon = Addon();
            return addon == null
                ? null
                : new System.Numerics.Vector2(addon->X + addon->GetScaledWidth(true), addon->Y);
        }
    }

    public SynthesisSnapshot? Read()
    {
        try
        {
            var addon = Addon();
            var player = _objects.LocalPlayer;
            var recipeNote = RecipeNote.Instance();
            if (addon == null || player == null || recipeNote == null)
                return null;

            var values = addon->AtkValues;
            int innerQuiet = 0, wasteNot = 0, innovation = 0, veneration = 0, greatStrides = 0, muscleMemory = 0, manipulation = 0;
            bool trainedPerfection = false, heartAndSoul = false, expedience = false;

            foreach (var status in player.StatusList)
            {
                switch (status.StatusId)
                {
                    case InnerQuiet: innerQuiet = status.Param; break;
                    case WasteNot or WasteNot2: wasteNot = status.Param; break;
                    case Innovation: innovation = status.Param; break;
                    case Veneration: veneration = status.Param; break;
                    case GreatStrides: greatStrides = status.Param; break;
                    case MuscleMemory: muscleMemory = status.Param; break;
                    case Manipulation: manipulation = status.Param; break;
                    case TrainedPerfection: trainedPerfection = true; break;
                    case HeartAndSoul: heartAndSoul = true; break;
                    case Expedience: expedience = true; break;
                }
            }

            var condition = values[ConditionIndex].Int;
            return new SynthesisSnapshot(
                RecipeId: recipeNote->ActiveCraftRecipeId,
                StepIndex: values[StepIndex].Int,
                Progress: values[Progress].Int,
                MaxProgress: values[MaxProgress].Int,
                Quality: values[Quality].Int,
                MaxQuality: values[MaxQuality].Int,
                Durability: values[Durability].Int,
                MaxDurability: values[MaxDurability].Int,
                Condition: condition is >= 0 and < ConditionOdds.ConditionCount ? (Condition)condition : Condition.Normal,
                Cp: (int)player.CurrentCp,
                MaxCp: (int)player.MaxCp,
                InnerQuiet: innerQuiet,
                WasteNot: wasteNot,
                Innovation: innovation,
                Veneration: veneration,
                GreatStrides: greatStrides,
                MuscleMemory: muscleMemory,
                Manipulation: manipulation,
                TrainedPerfectionActive: trainedPerfection,
                HeartAndSoulActive: heartAndSoul,
                Expedience: expedience);
        }
        catch
        {
            return null;
        }
    }

    private AtkUnitBase* Addon()
    {
        var addon = (AtkUnitBase*)_gameGui.GetAddonByName("Synthesis").Address;
        return addon != null && addon->IsVisible && addon->AtkValues != null && addon->AtkValuesCount >= MinAtkValues ? addon : null;
    }
}
