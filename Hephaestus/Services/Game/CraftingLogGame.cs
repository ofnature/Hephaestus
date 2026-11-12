using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Hephaestus.Features.Crafting;
using Item = Lumina.Excel.Sheets.Item;
using Recipe = Lumina.Excel.Sheets.Recipe;

namespace Hephaestus.Services.Game;

/// <summary>A material the selected recipe needs more of than the bags hold.</summary>
public readonly record struct MissingIngredient(string Name, int Have, int Need);

/// <summary>
/// Thin fail-open adapter over the crafting log (RecipeNote) and the Synthesis window: builds the
/// observation <see cref="CraftRun"/> decides on, and executes the commands it answers with.
/// The Cosmic Crafting Log (WKSRecipeNotebook) counts as the log when it is the one on screen: the same
/// recipe data behind it, its own Synthesize callback, button and "Craftable" number. NOT SEEN IN GAME
/// when written (2026-11-12) - the calls are Artisan's.
///
/// The game calls mirror what Artisan has field-verified (PunishXIV/Artisan, GameInterop/):
/// <c>AgentRecipeNote.OpenRecipeByRecipeId</c> to select a recipe, the NQ/HQ assignment bytes in
/// RecipeNote's recipe data to pick materials, callback <c>8</c> on RecipeNote to Synthesize, and
/// the Synthesis AtkValues for craft state. Where FFXIVClientStructs names a field we use it
/// instead of Artisan's raw offsets, so a patch breaks the build rather than the game.
/// </summary>
public sealed unsafe class CraftingLogGame
{
    // Synthesis AtkValues (Artisan CraftingLogic/CraftData/SynthesisAtkValues.cs + Crafting.cs getters).
    private const int SynthProgress = 5;
    private const int SynthMaxProgress = 6;
    private const int SynthDurability = 7;
    private const int SynthMinAtkValues = 26;

    /// <summary>RecipeNote callback that presses Synthesize (9 = Quick Synthesis).</summary>
    private const int SynthesizeCallback = 8;

    /// <summary>RecipeNote callback that closes the log — which is also how the crafting stance is left.</summary>
    private const int CloseCallback = -1;

    // The COSMIC Crafting Log (Cosmic Exploration missions) is a window of its own over the same recipe data
    // (Artisan, GameInterop/Operations.cs + UI/KTK/NativeCraftAll.cs, re-read from 4.0.5.23 on 2026-11-12):
    // callback 6 presses its Synthesize, text node 34 is its "Craftable" number, node 50 its Synthesize button.
    private const string CosmicLogName = "WKSRecipeNotebook";
    private const int CosmicSynthesizeCallback = 6;
    private const uint CosmicCraftableNode = 34;
    private const uint CosmicSynthesizeButtonNode = 50;

    /// <summary>Shards, crystals and clusters: item rows 2..19, kept in a container of their own.</summary>
    private const uint FirstCrystalItem = 2;
    private const uint LastCrystalItem = 19;

    /// <summary>EquippedItems slot holding the soul crystal, which has no condition to wear down.</summary>
    private const int SoulCrystalSlot = 13;

    // Gear used up as a material (Artisan, CraftingList/CraftingList.cs SetIngredients): the material rows are
    // component nodes 89..94; inside a row, node 7 is the "Unselected" marker and node 5 the row's button. A
    // ButtonClick (25) with param 5, aimed at that button, opens the ContextIconMenu picker; its callback
    // [0, entry, 0, item id, 0] takes an entry.
    private const uint FirstMaterialRow = 89;
    private const uint UnselectedMarker = 7;
    private const uint MaterialRowButton = 5;
    private const int MaterialRowButtonEvent = 5;
    private const AtkEventType ButtonClick = (AtkEventType)25;
    private const string PickerAddon = "ContextIconMenu";
    private const uint HqItemOffset = 1_000_000;

    /// <summary>A picker we opened is only ours for this long; any other one is somebody else's menu.</summary>
    private static readonly TimeSpan PickerWait = TimeSpan.FromSeconds(5);

    /// <summary>Where the log's picker finds copies of a piece of gear: the bags and the armoury chest.</summary>
    private static readonly InventoryType[] OfferedContainers =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    };

    private readonly record struct UnselectedRow(int Row, uint ItemId, GearHoldings Holdings);

    private readonly Dictionary<uint, bool> _isGear = new();
    private int _pickerRow = -1;
    private DateTime _pickerAskedAt;

    private readonly IGameGui _gameGui;
    private readonly ICondition _condition;
    private readonly IDataManager _data;

    public CraftingLogGame(IGameGui gameGui, ICondition condition, IDataManager data)
    {
        _gameGui = gameGui;
        _condition = condition;
        _data = data;
    }

    /// <param name="runRecipeId">The active run's recipe (0 = none) — its result item is what gets counted.</param>
    public CraftRunObservation Observe(uint runRecipeId)
    {
        try
        {
            var log = LogAddon();
            var cosmic = log == null ? CosmicLogAddon() : null;
            var selected = SelectedRecipe();
            var synthesis = SynthesisAddon();
            var gear = log == null || selected == null ? null : UnselectedGear(log, selected);

            return new CraftRunObservation(
                LogOpen: log != null || cosmic != null,
                SelectedRecipeId: selected == null ? 0u : selected->RecipeId,
                Craftable: selected == null ? -1
                    : log != null ? Craftable(log, selected->RecipeId)
                    : cosmic != null ? Math.Max(ReadCosmicCraftable(cosmic), ComputeCraftable(selected->RecipeId))
                    : -1,
                IngredientsAssigned: selected != null && AllAssigned(log, selected),
                SynthesisOpen: synthesis != null,
                Transitioning: _condition[ConditionFlag.ExecutingCraftingAction],
                Progress: synthesis == null ? 0 : synthesis->AtkValues[SynthProgress].Int,
                MaxProgress: synthesis == null ? 0 : synthesis->AtkValues[SynthMaxProgress].Int,
                Durability: synthesis == null ? 0 : synthesis->AtkValues[SynthDurability].Int,
                ResultItemCount: CountResultItem(runRecipeId),
                MinGearConditionPercent: MinGearConditionPercent(),
                GearMaterialUnselected: gear != null,
                GearMaterialRefusal: gear == null ? null : GearMaterial.Choose(gear.Value.Holdings).Refusal,
                ResultItemHqCount: CountResultItemHq(runRecipeId),
                CosmicLog: cosmic != null);
        }
        catch
        {
            // Fail open to "nothing is ready": the run waits, its repeat caps and deadlines halt it.
            return new CraftRunObservation(false, 0, -1, false, false, false, 0, 0, 0, 0, 100);
        }
    }

    /// <summary>Where a side window should sit: the top-right corner of the crafting log. Null when it is closed.</summary>
    public Vector2? LogTopRight
    {
        get
        {
            var log = LogAddon();
            var window = log != null ? &log->AtkUnitBase : CosmicLogAddon();
            return window == null
                ? null
                : new Vector2(window->X + window->GetScaledWidth(true), window->Y);
        }
    }

    /// <summary>
    /// Screen rectangle of the log's Synthesize button, for a control that rides inside the log
    /// the way Artisan's Craft All does. Null while the log is closed.
    /// </summary>
    public (Vector2 Position, Vector2 Size, float Scale)? SynthesizeButtonRect
    {
        get
        {
            var log = LogAddon();
            if (log == null)
                return CosmicSynthesizeButtonRect();
            if (log->SynthesizeButton == null)
                return null;

            var node = log->SynthesizeButton->AtkComponentBase.OwnerNode;
            if (node == null)
                return null;

            var scale = log->AtkUnitBase.Scale;
            return (
                new Vector2(node->AtkResNode.ScreenX, node->AtkResNode.ScreenY),
                new Vector2(node->AtkResNode.Width * scale, node->AtkResNode.Height * scale),
                scale);
        }
    }

    private (Vector2 Position, Vector2 Size, float Scale)? CosmicSynthesizeButtonRect()
    {
        var cosmic = CosmicLogAddon();
        var node = cosmic == null ? null : cosmic->GetNodeById(CosmicSynthesizeButtonNode);
        if (node == null)
            return null;

        var scale = cosmic->Scale;
        return (new Vector2(node->ScreenX, node->ScreenY), new Vector2(node->Width * scale, node->Height * scale), scale);
    }

    public string RecipeName(uint recipeId)
        => _data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId)?.ItemResult.ValueNullable?.Name.ExtractText()
           ?? $"recipe {recipeId}";

    /// <summary>NQ + HQ of an item in the bags (a crystal: from its own container). 0 when the bags cannot be read.</summary>
    public int HeldInBags(uint itemId)
    {
        try
        {
            var inventory = InventoryManager.Instance();
            return inventory == null ? 0 : Held(inventory, itemId);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Materials AND crystals the selected recipe is short of, for display — read the same way as
    /// the craftable count, so a max of 0 always has a line here explaining it.
    /// </summary>
    public List<MissingIngredient> MissingIngredients()
    {
        var missing = new List<MissingIngredient>();
        var selected = SelectedRecipe();
        var inventory = InventoryManager.Instance();
        if (selected == null || inventory == null
            || _data.GetExcelSheet<Recipe>().GetRowOrDefault(selected->RecipeId) is not { } recipe)
            return missing;

        for (var i = 0; i < recipe.Ingredient.Count; i++)
        {
            var itemId = recipe.Ingredient[i].RowId;
            int need = recipe.AmountIngredient[i];
            if (itemId == 0 || need == 0)
                continue;

            var have = Held(inventory, itemId);
            if (have < need)
            {
                var name = _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"item {itemId}";
                missing.Add(new MissingIngredient(name, have, need));
            }
        }

        return missing;
    }

    public void Execute(CraftRunCommand command, uint recipeId)
    {
        switch (command)
        {
            case CraftRunCommand.OpenRecipe:
                OpenRecipe(recipeId);
                break;
            case CraftRunCommand.AssignIngredients:
                AssignIngredients();
                break;
            case CraftRunCommand.ChooseGearMaterial:
                ChooseGearMaterial();
                break;
            case CraftRunCommand.PressSynthesize:
                PressSynthesize();
                break;
        }
    }

    private static void OpenRecipe(uint recipeId)
    {
        var agent = AgentRecipeNote.Instance();
        if (agent != null)
            agent->OpenRecipeByRecipeId(recipeId);
    }

    /// <summary>
    /// Write each ingredient's NQ/HQ split into the log's recipe data, from live bag counts.
    /// Artisan's Endurance does the same before every craft; the game reads these when Synthesize fires.
    /// </summary>
    private void AssignIngredients()
    {
        var selected = SelectedRecipe();
        var inventory = InventoryManager.Instance();
        if (selected == null || inventory == null)
            return;

        foreach (ref var ingredient in selected->Ingredients)
        {
            if (ingredient.ItemId == 0 || ingredient.Amount == 0)
                continue;
            if (IsGear(ingredient.ItemId))
                continue; // one particular item, chosen through the log's picker — a count written here would not be a choice

            var mix = IngredientAllocator.Allocate(
                ingredient.Amount,
                OnHand(inventory, ingredient.ItemId, hq: false),
                OnHand(inventory, ingredient.ItemId, hq: true));
            if (mix is not { } split)
                continue; // short of this one — the log reports 0 craftable and the run halts on that

            ingredient.NQCount = (byte)split.Nq;
            ingredient.HQCount = (byte)split.Hq;
        }
    }

    private void PressSynthesize() => Fire(SynthesizeCallback, CosmicSynthesizeCallback);

    /// <summary>Close the crafting log (either one). The game refuses a job change while it is open.</summary>
    public void CloseLog() => Fire(CloseCallback, CloseCallback);

    /// <summary>One callback to whichever log is open: the crafting log, else the Cosmic one.</summary>
    private void Fire(int callback, int cosmicCallback)
    {
        var log = LogAddon();
        var window = log != null ? &log->AtkUnitBase : CosmicLogAddon();
        if (window == null)
            return;

        var value = new AtkValue { Type = AtkValueType.Int, Int = log != null ? callback : cosmicCallback };
        window->FireCallback(1, &value, true);
    }

    private AddonRecipeNote* LogAddon()
    {
        var addon = (AddonRecipeNote*)_gameGui.GetAddonByName("RecipeNote").Address;
        return addon != null && addon->AtkUnitBase.IsVisible && addon->AtkUnitBase.IsReady ? addon : null;
    }

    /// <summary>The Cosmic Crafting Log, when it is the log on screen.</summary>
    private AtkUnitBase* CosmicLogAddon()
    {
        var addon = (AtkUnitBase*)_gameGui.GetAddonByName(CosmicLogName).Address;
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }

    /// <summary>The Cosmic log's own "Craftable" number; -1 when it cannot be read.</summary>
    private static int ReadCosmicCraftable(AtkUnitBase* cosmic)
    {
        var node = cosmic->GetTextNodeById(CosmicCraftableNode);
        if (node == null)
            return -1;

        var digits = new string(Array.FindAll(node->NodeText.ToString().ToCharArray(), char.IsDigit));
        return int.TryParse(digits, out var count) ? count : -1;
    }

    private AtkUnitBase* SynthesisAddon()
    {
        var addon = (AtkUnitBase*)_gameGui.GetAddonByName("Synthesis").Address;
        return addon != null && addon->AtkValues != null && addon->AtkValuesCount >= SynthMinAtkValues ? addon : null;
    }

    private static RecipeNote.RecipeEntry* SelectedRecipe()
    {
        var recipeNote = RecipeNote.Instance();
        if (recipeNote == null || recipeNote->RecipeList == null)
            return null;

        var list = recipeNote->RecipeList;
        return list->Recipes == null || list->SelectedIndex >= list->RecipeCount ? null : list->SelectedRecipe;
    }

    private bool AllAssigned(AddonRecipeNote* log, RecipeNote.RecipeEntry* recipe)
    {
        var ingredients = recipe->Ingredients;
        for (var i = 0; i < ingredients.Length; i++)
        {
            ref var ingredient = ref ingredients[i];
            if (ingredient.ItemId == 0)
                continue;

            if (IsGear(ingredient.ItemId))
            {
                // Assigned = chosen in the log; what the game writes for it is the game's business.
                if (log != null && Unselected(log, i))
                    return false;
                continue;
            }

            if (ingredient.NQCount + ingredient.HQCount != ingredient.Amount)
                return false;
        }

        return true;
    }

    // ---- gear used up as a material -------------------------------------------------------------------------------

    private bool IsGear(uint itemId)
    {
        if (!_isGear.TryGetValue(itemId, out var gear))
            _isGear[itemId] = gear = _data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is { } item && item.EquipSlotCategory.RowId != 0;
        return gear;
    }

    /// <summary>The material row shows "Unselected": the game is waiting for one particular item to be chosen.</summary>
    private static bool Unselected(AddonRecipeNote* log, int row)
    {
        var node = log->AtkUnitBase.GetComponentNodeById(FirstMaterialRow + (uint)row);
        if (node == null || !node->IsVisible() || node->Component == null)
            return false;

        var marker = node->Component->GetNodeById(UnselectedMarker);
        return marker != null && marker->IsVisible();
    }

    /// <summary>The first material still waiting to be chosen, with everything the safety rule needs to know about it.</summary>
    private UnselectedRow? UnselectedGear(AddonRecipeNote* log, RecipeNote.RecipeEntry* recipe)
    {
        var ingredients = recipe->Ingredients;
        for (var i = 0; i < ingredients.Length; i++)
        {
            ref var ingredient = ref ingredients[i];
            if (ingredient.ItemId == 0 || ingredient.Amount == 0 || !Unselected(log, i))
                continue;

            return new UnselectedRow(i, ingredient.ItemId, Holdings(ingredient.ItemId, ingredient.Amount));
        }

        return null;
    }

    private GearHoldings Holdings(uint itemId, int needed)
    {
        var offered = new List<GearCopy>();
        var wornNq = 0;
        var wornHq = 0;
        var inventory = InventoryManager.Instance();
        if (inventory != null)
        {
            foreach (var type in OfferedContainers)
            {
                var container = inventory->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot != null && slot->ItemId == itemId && slot->Quantity > 0)
                        offered.Add(new GearCopy(slot->IsHighQuality(), slot->GetMateriaCount() > 0));
                }
            }

            var worn = inventory->GetInventoryContainer(InventoryType.EquippedItems);
            for (var i = 0; worn != null && i < worn->Size; i++)
            {
                var slot = worn->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId)
                    continue;
                if (slot->IsHighQuality())
                    wornHq++;
                else
                    wornNq++;
            }
        }

        var (wearsNq, wearsHq) = GearsetsWearing(itemId);
        var name = _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"item {itemId}";
        return new GearHoldings(name, needed, offered, wornNq, wornHq, wearsNq, wearsHq);
    }

    /// <summary>Whether any saved gearset wears this item, by quality (a gearset names HQ as the item id + 1,000,000).</summary>
    private static (bool Nq, bool Hq) GearsetsWearing(uint itemId)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return (false, false);

        var nq = false;
        var hq = false;
        for (var i = 0; i < module->NumGearsets; i++)
        {
            if (!module->IsValidGearset(i))
                continue;

            var gearset = module->GetGearset(i);
            if (gearset == null)
                continue;

            foreach (ref var item in gearset->Items)
            {
                if (item.ItemId == itemId)
                    nq = true;
                else if (item.ItemId == itemId + HqItemOffset)
                    hq = true;
            }
        }

        return (nq, hq);
    }

    /// <summary>
    /// Two steps, one per call: open the row's picker, then take an entry of it. Only when the rule says every
    /// offered copy is safe to lose; the run re-reads the row afterwards, which is the only proof it took.
    /// </summary>
    private void ChooseGearMaterial()
    {
        var log = LogAddon();
        var selected = SelectedRecipe();
        if (log == null || selected == null || UnselectedGear(log, selected) is not { } gear)
            return;

        var choice = GearMaterial.Choose(gear.Holdings);
        if (!choice.Safe)
            return; // the run halts on the refusal it was shown

        var now = DateTime.UtcNow;
        var picker = (AddonContextIconMenu*)_gameGui.GetAddonByName(PickerAddon).Address;
        if (picker != null && picker->AtkUnitBase.IsVisible)
        {
            if (_pickerRow != gear.Row || now - _pickerAskedAt > PickerWait)
                return; // not a picker we opened: somebody else's menu is left alone

            var values = stackalloc AtkValue[5];
            values[0].SetInt(0);
            values[1].SetInt(GearMaterial.Entry(choice, gear.Holdings, picker->EntryCount));
            values[2].SetInt(0);
            values[3].SetUInt(gear.ItemId);
            values[4] = default;
            picker->AtkUnitBase.FireCallback(5, values, true);
            _pickerRow = -1;
            return;
        }

        var row = log->AtkUnitBase.GetComponentNodeById(FirstMaterialRow + (uint)gear.Row);
        var button = row == null || row->Component == null ? null : row->Component->GetNodeById(MaterialRowButton);
        if (button == null)
            return;

        var evt = new AtkEvent { Listener = (AtkEventListener*)log, Target = (AtkEventTarget*)button };
        var data = default(AtkEventData);
        log->AtkUnitBase.ReceiveEvent(ButtonClick, MaterialRowButtonEvent, &evt, &data);
        _pickerRow = gear.Row;
        _pickerAskedAt = now;
    }

    /// <summary>
    /// How many of the selected recipe the bags allow. Two readings, the larger wins: OURS, from the
    /// Recipe sheet's inputs (materials AND crystals) against bag counts — which does not depend on
    /// the job the character is on — and the LOG'S own number. They should agree; taking the larger
    /// means a reading that comes up short (a count we cannot see, a log that shows nothing for
    /// another job's recipe) cannot switch crafting off. Too high is safe: Synthesize is refused and
    /// the run halts on its failed-start cap. -1 when neither can be read.
    /// </summary>
    private int Craftable(AddonRecipeNote* log, uint recipeId) => Math.Max(ReadCraftable(log), ComputeCraftable(recipeId));

    private int ComputeCraftable(uint recipeId)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null || _data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId) is not { } recipe)
            return -1;

        // The sheet lists crystals as inputs like any other (the log keeps them apart, by crystal index).
        Span<(int Need, int Have)> inputs = stackalloc (int, int)[recipe.Ingredient.Count];
        for (var i = 0; i < inputs.Length; i++)
        {
            var itemId = recipe.Ingredient[i].RowId;
            inputs[i] = itemId == 0
                ? (0, 0)
                : (recipe.AmountIngredient[i], Held(inventory, itemId));
        }

        return CraftableCount.From(inputs);
    }

    /// <summary>The log's own "Craftable" number for the selected recipe; -1 when it cannot be read.</summary>
    private static int ReadCraftable(AddonRecipeNote* log)
    {
        var node = log->SelectedRecipeQuantityCraftableFromMaterialsInInventory;
        if (node == null)
            return -1;

        var digits = new string(Array.FindAll(node->NodeText.ToString().ToCharArray(), char.IsDigit));
        return int.TryParse(digits, out var count) ? count : -1;
    }

    private int CountResultItem(uint recipeId)
    {
        var inventory = InventoryManager.Instance();
        if (recipeId == 0 || inventory == null)
            return 0;

        var itemId = _data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId)?.ItemResult.RowId ?? 0;
        return itemId == 0 ? 0 : OnHand(inventory, itemId, hq: false) + OnHand(inventory, itemId, hq: true);
    }

    /// <summary>HQ count of a run's result item, split out so completion can tell HQ from NQ.</summary>
    private int CountResultItemHq(uint recipeId)
    {
        var inventory = InventoryManager.Instance();
        if (recipeId == 0 || inventory == null)
            return 0;

        var itemId = _data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId)?.ItemResult.RowId ?? 0;
        return itemId == 0 ? 0 : OnHand(inventory, itemId, hq: true);
    }

    /// <summary>
    /// Everything of an item a craft could use: NQ + HQ in the bags, or — for a shard, crystal or
    /// cluster — its slot in the crystal container, read directly (the way Odysseus counts shards,
    /// where leaving that container out was a field-found bug).
    /// </summary>
    private static int Held(InventoryManager* inventory, uint itemId)
    {
        if (itemId is < FirstCrystalItem or > LastCrystalItem)
            return OnHand(inventory, itemId, hq: false) + OnHand(inventory, itemId, hq: true);

        var crystals = inventory->GetInventoryContainer(InventoryType.Crystals);
        if (crystals == null || !crystals->IsLoaded)
            return 0;

        for (var i = 0; i < crystals->Size; i++)
        {
            var slot = crystals->GetInventorySlot(i);
            if (slot != null && slot->ItemId == itemId)
                return (int)slot->Quantity;
        }

        return 0;
    }

    /// <summary>Bags only — equipped and armoury copies are not crafting materials or fresh results.</summary>
    private static int OnHand(InventoryManager* inventory, uint itemId, bool hq)
        => inventory->GetInventoryItemCount(itemId, hq, false, false);

    private static int MinGearConditionPercent()
    {
        var inventory = InventoryManager.Instance();
        var equipped = inventory == null ? null : inventory->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped == null)
            return 100;

        var min = 100;
        for (var i = 0; i < equipped->Size; i++)
        {
            if (i == SoulCrystalSlot)
                continue;

            var slot = equipped->GetInventorySlot(i);
            if (slot != null && slot->ItemId != 0)
                min = Math.Min(min, slot->GetConditionPercentage());
        }

        return min;
    }
}
