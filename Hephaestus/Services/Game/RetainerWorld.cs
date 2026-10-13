using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Hephaestus.Features.Materials;

namespace Hephaestus.Services.Game;

/// <summary>
/// The game side of a <see cref="RetainerTrip"/>. Thin and fail-open. The calls are the ones GBR's
/// Vulcan proved at the bell (RetainerTaskExecutor): the RetainerList callback <c>2, sortedIndex</c>,
/// the retainer's SelectString by text, <c>InputNumeric</c>'s callback with the count, and the
/// retainer agent hidden to close the bags — with one difference: the Retrieve command goes through
/// ClientStructs' typed <c>AgentRetainer.HandleCallback</c> instead of a raw vtable offset.
/// </summary>
public sealed unsafe class RetainerWorld : IRetainerWorld
{
    /// <summary>City plaza aetherytes with a Summoning Bell beside them, tried in this order.</summary>
    private static readonly (uint AetheryteId, uint Territory)[] BellPlazas =
    {
        (8, 129),  // Limsa Lominsa Lower Decks
        (2, 132),  // New Gridania
        (9, 130),  // Ul'dah - Steps of Nald
    };

    private static readonly InventoryType[] Pages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    };

    /// <summary>Every EObj row named "summoning bell" in the EObjName sheet (= the object's BaseId), read 2026-09-26.</summary>
    private static readonly HashSet<uint> BellObjects = new()
    {
        2000072, 2000401, 2000403, 2000439, 2000441, 2000661, 2001271, 2001358, 2006565, 2010284, 2014985, 2015131,
    };

    private const ulong RetrieveCommand = 0;
    private const ulong RetrieveQuantityCommand = 3;
    private const uint MountRouletteAction = 9;
    private const uint DismountAction = 23;

    private readonly IClientState _clientState;
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objects;
    private readonly ICondition _condition;
    private readonly CraftingLogGame _bags;
    private readonly TravelCatalog _travel;
    private readonly NavClient _nav;
    private readonly LifestreamClient _lifestream;
    private readonly IPluginLog _log;

    public RetainerWorld(
        IClientState clientState, IGameGui gameGui, IObjectTable objects, ICondition condition, CraftingLogGame bags,
        TravelCatalog travel, NavClient nav, LifestreamClient lifestream, IPluginLog log)
    {
        _clientState = clientState;
        _gameGui = gameGui;
        _objects = objects;
        _condition = condition;
        _bags = bags;
        _travel = travel;
        _nav = nav;
        _lifestream = lifestream;
        _log = log;
    }

    public uint Territory => _clientState.TerritoryType;

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

    public bool AtABell => _condition[ConditionFlag.OccupiedSummoningBell];

    /// <summary>By object id, so it holds in any client language (GBR matches the English name).</summary>
    public Vector3? NearestBell()
    {
        var player = _objects.LocalPlayer;
        if (player == null)
            return null;

        Vector3? nearest = null;
        var best = float.MaxValue;
        foreach (var obj in _objects)
        {
            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject) || !obj.IsTargetable || !BellObjects.Contains(obj.BaseId))
                continue;

            var distance = Vector3.Distance(obj.Position, player.Position);
            if (distance < best)
                (nearest, best) = (obj.Position, distance);
        }

        return nearest;
    }

    public TravelRoute? RouteToABell()
    {
        foreach (var (aetheryte, territory) in BellPlazas)
        {
            if (_travel.IsAttuned(aetheryte))
                return new TravelRoute(aetheryte, territory, 0, territory);
        }

        return null;
    }

    public bool Teleport(uint aetheryteId) => _lifestream.Teleport(aetheryteId);

    public bool AethernetHop(uint placeNameId) => _lifestream.AethernetHop(placeNameId);

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

    public bool InteractWithBell()
    {
        try
        {
            var player = _objects.LocalPlayer;
            if (player == null)
                return false;

            Dalamud.Game.ClientState.Objects.Types.IGameObject? bell = null;
            var best = float.MaxValue;
            foreach (var obj in _objects)
            {
                if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.HousingEventObject) || !obj.IsTargetable || !BellObjects.Contains(obj.BaseId))
                    continue;

                var distance = Vector3.Distance(obj.Position, player.Position);
                if (distance < best)
                    (bell, best) = (obj, distance);
            }

            if (bell == null)
                return false;

            TargetSystem.Instance()->OpenObjectInteraction((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)bell.Address);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: opening the summoning bell failed");
            return false;
        }
    }

    public bool AdvanceTalk() => AddonClicks.AdvanceTalk(_gameGui);

    public bool RetainerListOpen
    {
        get
        {
            var addon = _gameGui.GetAddonByName("RetainerList");
            return !addon.IsNull && addon.IsVisible && ((AtkUnitBase*)addon.Address)->IsReady;
        }
    }

    public IReadOnlyList<RetainerSlot> Roster
    {
        get
        {
            var roster = new List<RetainerSlot>();
            try
            {
                var manager = RetainerManager.Instance();
                if (manager == null || !manager->IsReady)
                    return roster;

                foreach (var retainer in manager->Retainers)
                {
                    if (retainer.RetainerId != 0)
                        roster.Add(new RetainerSlot(retainer.RetainerId, retainer.NameString, retainer.Available));
                }
            }
            catch
            {
                // an unreadable roster is an empty one
            }

            return roster;
        }
    }

    /// <summary>The list's callback takes the SORTED index (the order the window shows), so the id is mapped through it.</summary>
    public bool SelectRetainer(ulong retainerId)
    {
        try
        {
            var manager = RetainerManager.Instance();
            var addon = _gameGui.GetAddonByName("RetainerList");
            if (manager == null || addon.IsNull || !addon.IsVisible)
                return false;

            for (uint sorted = 0; sorted < manager->GetRetainerCount(); sorted++)
            {
                var retainer = manager->GetRetainerBySortedIndex(sorted);
                if (retainer == null || retainer->RetainerId != retainerId)
                    continue;

                var values = stackalloc AtkValue[2];
                values[0] = new AtkValue { Type = AtkValueType.Int, Int = 2 };
                values[1] = new AtkValue { Type = AtkValueType.UInt, UInt = sorted };
                ((AtkUnitBase*)addon.Address)->FireCallback(2, values, true);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: selecting retainer {0} failed", retainerId);
            return false;
        }
    }

    public bool MenuOpen => AddonClicks.SelectString(_gameGui) != null;

    public bool ChooseMenuEntry(string containing, int fallbackIndex)
    {
        var menu = AddonClicks.SelectString(_gameGui);
        return menu != null && AddonClicks.ChooseEntry(menu, containing, fallbackIndex);
    }

    public bool BagsOpen
    {
        get
        {
            try
            {
                var agent = AgentRetainer.Instance();
                if (agent == null || !agent->IsAgentActive())
                    return false;

                var inventory = InventoryManager.Instance();
                if (inventory == null)
                    return false;

                foreach (var page in Pages)
                {
                    var container = inventory->GetInventoryContainer(page);
                    if (container == null || !container->IsLoaded)
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public ItemCount InRetainerBags(uint itemId)
    {
        var count = new ItemCount(0, 0);
        try
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null)
                return count;

            foreach (var page in Pages)
            {
                var container = inventory->GetInventoryContainer(page);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId)
                        continue;

                    count = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)
                        ? count with { Hq = count.Hq + (int)slot->Quantity }
                        : count with { Nq = count.Nq + (int)slot->Quantity };
                }
            }
        }
        catch
        {
            // fail open to nothing
        }

        return count;
    }

    public int Retrieve(uint itemId, bool hq, int count, out bool asksQuantity)
    {
        asksQuantity = false;
        try
        {
            var agent = AgentRetainer.Instance();
            var inventory = InventoryManager.Instance();
            if (agent == null || !agent->IsAgentActive() || inventory == null || count <= 0)
                return 0;

            foreach (var page in Pages)
            {
                var container = inventory->GetInventoryContainer(page);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != hq)
                        continue;

                    var stack = (int)slot->Quantity;
                    if (stack <= count)
                    {
                        agent->HandleCallback((uint)i, page, 0, RetrieveCommand);
                        return stack;
                    }

                    asksQuantity = true;
                    agent->HandleCallback((uint)i, page, 0, RetrieveQuantityCommand);
                    return count;
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: retrieving item {0} from a retainer failed", itemId);
            return 0;
        }
    }

    public bool NumericOpen
    {
        get
        {
            var addon = _gameGui.GetAddonByName("InputNumeric");
            return !addon.IsNull && addon.IsVisible;
        }
    }

    public bool EnterQuantity(int quantity)
    {
        var addon = _gameGui.GetAddonByName("InputNumeric");
        return !addon.IsNull && addon.IsVisible && ((AtkUnitBase*)addon.Address)->FireCallbackInt(quantity);
    }

    public void CloseBags()
    {
        try
        {
            var agent = AgentRetainer.Instance();
            if (agent != null && agent->IsAgentActive())
                agent->Hide();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: closing the retainer's bags failed");
        }
    }

    public void CloseRetainerList()
    {
        var addon = _gameGui.GetAddonByName("RetainerList");
        if (!addon.IsNull && addon.IsVisible)
            ((AtkUnitBase*)addon.Address)->FireCallbackInt(-1);
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
