using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Hephaestus.Features.Materials;
using Item = Lumina.Excel.Sheets.Item;

namespace Hephaestus.Services.Game;

/// <summary>
/// The game side of a <see cref="ChestTrip"/>. Thin and fail-open. The chest calls are Odysseus's
/// <c>GameChestWorld</c> (field-proven): stacks off the loaded <c>FreeCompanyPage</c> containers, a whole-stack
/// <c>MoveItemSlot</c> into a same-item stack that takes ALL of it or the first empty slot, and success read
/// off the source slot. The tab walk is Charon's: the addon's own tab-click callback. The estate trip is
/// Lifestream's <c>/li fc</c>, issued as the Dalamud command it registers. The chest itself is EObj 2000470.
/// </summary>
public sealed unsafe class ChestWorld : IChestWorld
{
    private const string ChestAddon = "FreeCompanyChest";

    /// <summary>EObj row (= the object's BaseId) of the Company Chest, from the EObjName sheet.</summary>
    private const uint CompanyChestObject = 2000470;

    private const uint MountRouletteAction = 9;
    private const uint DismountAction = 23;

    private static readonly InventoryType[] Pages =
    {
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5,
    };

    private static readonly InventoryType[] Bags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    };

    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objects;
    private readonly ICondition _condition;
    private readonly ICommandManager _commands;
    private readonly IDataManager _data;
    private readonly CraftingLogGame _bags;
    private readonly NavClient _nav;
    private readonly LifestreamClient _lifestream;
    private readonly IPluginLog _log;

    /// <summary>
    /// The split move's return slot and four values. They outlive the call on purpose: the "how many?"
    /// prompt reads the move back when it is answered, after the call has returned. One move runs at a
    /// time, so one buffer, allocated once, does.
    /// </summary>
    private readonly AtkValue* _moveValues = (AtkValue*)NativeMemory.AllocZeroed(5, (nuint)sizeof(AtkValue));

    public ChestWorld(
        IGameGui gameGui, IObjectTable objects, ICondition condition, ICommandManager commands, IDataManager data,
        CraftingLogGame bags, NavClient nav, LifestreamClient lifestream, IPluginLog log)
    {
        _gameGui = gameGui;
        _objects = objects;
        _condition = condition;
        _commands = commands;
        _data = data;
        _bags = bags;
        _nav = nav;
        _lifestream = lifestream;
        _log = log;
    }

    public bool Ready
        => _objects.LocalPlayer != null
           && !_condition[ConditionFlag.BetweenAreas]
           && !_condition[ConditionFlag.BetweenAreas51]
           && !_condition[ConditionFlag.Casting]
           && !_condition[ConditionFlag.Occupied]
           && !_condition[ConditionFlag.OccupiedInEvent]
           && !_condition[ConditionFlag.OccupiedInQuestEvent]
           && !_condition[ConditionFlag.OccupiedInCutSceneEvent]
           && !_condition[ConditionFlag.InCombat]
           && !_condition[ConditionFlag.Crafting]
           && !_condition[ConditionFlag.PreparingToCraft];

    public bool Travelling
        => _lifestream.Busy || _condition[ConditionFlag.Casting] || _condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51];

    public Vector3 PlayerPosition => _objects.LocalPlayer?.Position ?? Vector3.Zero;

    public int Held(uint itemId) => _bags.HeldInBags(itemId);

    public int FreeBagSlots
    {
        get
        {
            try
            {
                var inventory = InventoryManager.Instance();
                return inventory == null ? int.MaxValue : (int)inventory->GetEmptySlotsInBag();
            }
            catch
            {
                return int.MaxValue;
            }
        }
    }

    public Vector3? NearestChest() => FindChest()?.Position;

    public bool CanGoToEstate => _lifestream.Loaded;

    public bool GoToEstate()
    {
        try
        {
            return _commands.ProcessCommand("/li fc");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: /li fc failed");
            return false;
        }
    }

    public void AbortTravel() => _lifestream.Abort();

    public bool CanWalk => _nav.Ready;

    public bool Walking => _nav.Busy;

    public bool WalkTo(Vector3 destination, float within) => _nav.MoveCloseTo(destination, within);

    public void StopWalking() => _nav.Stop();

    public bool Mounted => _condition[ConditionFlag.Mounted];

    public bool CanMount
    {
        get
        {
            try
            {
                var manager = ActionManager.Instance();
                return manager != null && manager->GetActionStatus(ActionType.GeneralAction, MountRouletteAction) == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Mount() => UseGeneralAction(MountRouletteAction);

    public void Dismount() => UseGeneralAction(DismountAction);

    public bool InteractWithChest()
    {
        try
        {
            var chest = FindChest();
            if (chest == null)
                return false;

            TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)chest.Address);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: opening the company chest failed");
            return false;
        }
    }

    public bool AdvanceTalk() => AddonClicks.AdvanceTalk(_gameGui);

    public bool ChestOpen
    {
        get
        {
            var addon = _gameGui.GetAddonByName(ChestAddon);
            return !addon.IsNull && addon.IsVisible;
        }
    }

    public bool PageLoaded(int page)
    {
        try
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(Pages[page - 1]);
            return container != null && container->IsLoaded;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The addon's own tab click: two ints, 0 and the page index 0..4 (Charon's FcChestUi.SwitchToPage).</summary>
    public bool ViewPage(int page)
    {
        var addon = _gameGui.GetAddonByName(ChestAddon);
        if (addon.IsNull || !addon.IsVisible || page < 1 || page > Pages.Length)
            return false;

        // Charon's field-verified shape: SetInt on both values, updateState false.
        var values = stackalloc AtkValue[2];
        values[0].SetInt(0);
        values[1].SetInt(page - 1);
        ((AtkUnitBase*)addon.Address)->FireCallback(2, values, false);
        return true;
    }

    /// <summary>How many of an item one LOADED page shows right now — live, so current after our own moves.</summary>
    public int HeldOnPage(uint itemId, int page)
    {
        var total = 0;
        foreach (var stack in ChestStacks(itemId))
        {
            if (stack.Page == page)
                total += stack.Quantity;
        }

        return total;
    }

    public IReadOnlyList<ChestStack> ChestStacks(uint itemId)
    {
        var stacks = new List<ChestStack>();
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return stacks;

            for (var page = 0; page < Pages.Length; page++)
            {
                var container = manager->GetInventoryContainer(Pages[page]);
                if (container == null || !container->IsLoaded)
                    continue; // a page loads when its tab is first viewed; an unviewed one reads as empty

                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId != itemId || item->Quantity == 0)
                        continue;

                    stacks.Add(new ChestStack(page + 1, slot, itemId, (int)item->Quantity, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Hephaestus: reading the chest for item {0} failed", itemId);
        }

        return stacks;
    }

    /// <summary>
    /// Move a whole stack into the bags: into a same-item stack that can take all of it, else the first
    /// empty slot. A strict fit only — a partial merge would leave a remainder behind that the caller's
    /// "did the slot empty" check would then read as a failure.
    /// </summary>
    public bool Withdraw(ChestStack stack)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null)
                return false;

            var source = Pages[stack.Page - 1];
            var container = manager->GetInventoryContainer(source);
            if (container == null || !container->IsLoaded || stack.Slot >= container->Size)
                return false;

            var flags = container->GetInventorySlot(stack.Slot)->Flags;
            if (!Destination(manager, stack, stack.Quantity, flags, out var type, out var slot))
                return false;

            manager->MoveItemSlot(source, (ushort)stack.Slot, type, (ushort)slot, true);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: withdrawing item {0} from the chest failed", stack.ItemId);
            return false;
        }
    }

    /// <summary>
    /// Part of a stack, the way a player does it: <c>RaptureAtkModule.HandleItemMove</c> with
    /// [source inventory type, source slot, destination type, destination slot]. For a stack of more
    /// than one the game asks how many; <see cref="AnswerQuantity"/> replies.
    /// </summary>
    public bool WithdrawSome(ChestStack stack, int amount)
    {
        try
        {
            var manager = InventoryManager.Instance();
            var module = RaptureAtkModule.Instance();
            if (manager == null || module == null)
                return false;

            var source = Pages[stack.Page - 1];
            var container = manager->GetInventoryContainer(source);
            if (container == null || !container->IsLoaded || stack.Slot >= container->Size)
                return false;

            var flags = container->GetInventorySlot(stack.Slot)->Flags;
            if (!Destination(manager, stack, amount, flags, out var type, out var slot))
                return false;

            var values = _moveValues + 1;
            values[0].SetInt((int)source);
            values[1].SetInt(stack.Slot);
            values[2].SetInt((int)type);
            values[3].SetInt(slot);
            module->HandleItemMove(_moveValues, values, 4);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: withdrawing {0} of item {1} from the chest failed", amount, stack.ItemId);
            return false;
        }
    }

    public bool QuantityPromptOpen
    {
        get
        {
            var addon = _gameGui.GetAddonByName("InputNumeric");
            return !addon.IsNull && addon.IsVisible;
        }
    }

    /// <summary>OK with this many — the prompt's own callback, recorded as [Int n] (Cancel is -1).</summary>
    public void AnswerQuantity(int amount)
    {
        try
        {
            var unit = (AtkUnitBase*)_gameGui.GetAddonByName("InputNumeric").Address;
            if (unit == null)
                return;

            var value = stackalloc AtkValue[1];
            value[0].SetInt(amount);
            unit->FireCallback(1, value, true);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: answering the chest's quantity prompt failed");
        }
    }

    /// <summary>What is left in the stack's slot. Success is it shrinking by what was asked for, never the return code.</summary>
    public int QuantityAt(ChestStack stack)
    {
        try
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(Pages[stack.Page - 1]);
            if (container == null || !container->IsLoaded || stack.Slot >= container->Size)
                return 0;

            var item = container->GetInventorySlot(stack.Slot);
            return item == null || item->ItemId != stack.ItemId ? 0 : (int)item->Quantity;
        }
        catch
        {
            return 0; // unreadable mid-transition; the trip's own bag count is the backstop
        }
    }

    public void CloseChest()
    {
        var addon = _gameGui.GetAddonByName(ChestAddon);
        if (!addon.IsNull && addon.IsVisible)
            ((AtkUnitBase*)addon.Address)->Close(true);
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindChest()
    {
        var player = _objects.LocalPlayer;
        if (player == null)
            return null;

        Dalamud.Game.ClientState.Objects.Types.IGameObject? nearest = null;
        var best = float.MaxValue;
        foreach (var obj in _objects)
        {
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject) || obj.BaseId != CompanyChestObject || !obj.IsTargetable)
                continue;

            var distance = Vector3.Distance(obj.Position, player.Position);
            if (distance < best)
                (nearest, best) = (obj, distance);
        }

        return nearest;
    }

    private bool Destination(InventoryManager* manager, ChestStack stack, int amount, InventoryItem.ItemFlags flags, out InventoryType type, out int slot)
    {
        var maxStack = MaxStack(stack.ItemId);
        foreach (var bag in Bags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null || item->ItemId != stack.ItemId || item->Quantity == 0 || item->Flags != flags)
                    continue;
                if (item->Quantity + amount > maxStack)
                    continue; // merge only when the whole amount fits

                type = bag;
                slot = i;
                return true;
            }
        }

        foreach (var bag in Bags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId == 0)
                {
                    type = bag;
                    slot = i;
                    return true;
                }
            }
        }

        type = InventoryType.Inventory1;
        slot = -1;
        return false;
    }

    /// <summary>Stack ceiling for an item; 1 when it cannot be read, so nothing ever merges blindly.</summary>
    private uint MaxStack(uint itemId)
    {
        try
        {
            return _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.StackSize ?? 1u;
        }
        catch
        {
            return 1;
        }
    }

    private void UseGeneralAction(uint id)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager != null)
                manager->UseAction(ActionType.GeneralAction, id);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: general action {0} failed", id);
        }
    }
}
