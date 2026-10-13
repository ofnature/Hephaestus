using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Hephaestus.Features.Materials;
using EventHandler = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler;

namespace Hephaestus.Services.Game;

/// <summary>
/// The game side of a <see cref="VendorTrip"/>. Thin and fail-open.
///
/// The shop calls are Odysseus's, field-proven there (Services/Deliveries/DeliveryWorld.cs): a shop
/// is an event handler, not an addon — interact with the NPC, pick the shop out of the event
/// selector when they offer several things, and buy through <c>ShopEventHandler.ExecuteBuy</c>
/// directly, which sets the quantity without touching the window's stepper. Whether a purchase
/// WORKED is not decided here: the trip re-reads the bag.
///
/// Vendors behind dialogue: a greeting (<c>Talk</c>) is clicked through the way Odysseus does it
/// (ported from ECommons' AddonMaster.Talk, MIT). A scripted menu the game's own selector does not
/// back (<c>SelectIconString</c>/<c>SelectString</c> from a CustomTalk) is NOT clicked: its options
/// are script strings and a guess on an unattended box is not safe — it is reported and closed, and
/// the trip strikes the seller off. GBR's Vulcan walks four sheets to map those menus; that is a
/// later step if such vendors turn out to matter.
/// </summary>
public sealed unsafe class VendorWorld : IVendorWorld
{
    /// <summary>Gil is item 1 as far as the inventory is concerned.</summary>
    private const uint GilItemId = 1;

    /// <summary>General actions 9 (Mount Roulette) and 23 (Dismount) — verified against the sheet by Odysseus.</summary>
    private const uint MountRouletteAction = 9;
    private const uint DismountAction = 23;

    private readonly IClientState _clientState;
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objects;
    private readonly ICondition _condition;
    private readonly CraftingLogGame _bags;
    private readonly VendorCatalog _catalog;
    private readonly VendorLocations _locations;
    private readonly TravelCatalog _travel;
    private readonly NavClient _nav;
    private readonly LifestreamClient _lifestream;
    private readonly IPluginLog _log;
    private readonly Dictionary<uint, IReadOnlyList<Seller>> _sellers = new();

    public VendorWorld(
        IClientState clientState, IGameGui gameGui, IObjectTable objects, ICondition condition, CraftingLogGame bags, VendorCatalog catalog,
        VendorLocations locations, TravelCatalog travel, NavClient nav, LifestreamClient lifestream, IPluginLog log)
    {
        _clientState = clientState;
        _gameGui = gameGui;
        _objects = objects;
        _condition = condition;
        _bags = bags;
        _catalog = catalog;
        _locations = locations;
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
        => _lifestream.Busy
           || _condition[ConditionFlag.Casting]
           || _condition[ConditionFlag.BetweenAreas]
           || _condition[ConditionFlag.BetweenAreas51];

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
                return int.MaxValue; // unreadable is not "full": do not pause the queue on a bad read
            }
        }
    }

    public long Gil => _bags.HeldInBags(GilItemId);

    public uint PriceOf(uint itemId) => _catalog.PriceOf(itemId);

    public IReadOnlyList<Seller> SellersOf(uint itemId)
    {
        if (!_sellers.TryGetValue(itemId, out var sellers))
        {
            var list = new List<Seller>();
            foreach (var vendor in _catalog.VendorsFor(itemId))
                list.Add(new Seller(vendor.NpcDataId, vendor.ShopId));
            _sellers[itemId] = sellers = list;
        }

        return sellers;
    }

    public Vector3? SpawnedAt(uint npcDataId)
    {
        var npc = FindObject(npcDataId);
        return npc == null ? null : (Vector3)npc->Position;
    }

    public (uint Territory, Vector3 Position)? PlacedAt(uint npcDataId)
        => _locations.Find(npcDataId) is { } at ? (at.TerritoryId, at.Position) : null;

    public bool PlacesKnown => _locations.Ready;

    public TravelRoute? RouteTo(uint territory, Vector3 near) => _travel.RouteTo(territory, near, Territory);

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

    public TravelRoute? ShortcutTo(Vector3 destination) => _travel.HopWithin(Territory, PlayerPosition, destination);

    /// <summary>Called once a second until the shop is open, so each call deals with whatever is on screen now.</summary>
    public bool OpenShop(uint npcDataId, uint shopId)
    {
        try
        {
            var npc = FindObject(npcDataId);
            if (npc == null)
                return false;

            // A greeting line: click it away; the shop, or the selector, follows.
            if (AdvanceTalk())
                return true;

            // The selector left up by the last call (an NPC with several purposes).
            if (SelectorFor(npc) is var shown && shown != null)
                return PickShop(shown, npcDataId, shopId);

            if (ScriptedMenu() is { } menu)
            {
                _log.Warning("Hephaestus: NPC {0} put up a {1} the vendor trip cannot read (shop {2:X}) — closing it; buy from them by hand", npcDataId, menu, shopId);
                CloseScriptedMenu();
                return false;
            }

            TargetSystem.Instance()->InteractWithObject(npc);

            // An NPC with one purpose opens straight away; one with several puts up a selector.
            var selector = SelectorFor(npc);
            return selector == null || PickShop(selector, npcDataId, shopId);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: opening shop {0:X} failed", shopId);
            return false;
        }
    }

    private static EventHandlerSelector* SelectorFor(GameObject* npc)
    {
        var selector = EventHandlerSelector.Instance();
        return selector != null && selector->Target == npc ? selector : null;
    }

    private bool PickShop(EventHandlerSelector* selector, uint npcDataId, uint shopId)
    {
        for (var i = 0; i < selector->OptionsCount; i++)
        {
            var handler = selector->Options[i].Handler;
            if (handler != null && handler->Info.EventId.Id == shopId)
            {
                EventFramework.Instance()->InteractWithHandlerFromSelector(i);
                return true;
            }
        }

        _log.Debug("Hephaestus: shop {0:X} is not among what NPC {1} offers", shopId, npcDataId);
        return false;
    }

    private bool AdvanceTalk() => AddonClicks.AdvanceTalk(_gameGui);

    /// <summary>A visible SelectIconString / SelectString, described; null when neither is up.</summary>
    private string? ScriptedMenu()
    {
        var icon = _gameGui.GetAddonByName("SelectIconString");
        if (!icon.IsNull && icon.IsVisible)
            return $"SelectIconString with {((AddonSelectIconString*)icon.Address)->PopupMenu.PopupMenu.EntryCount} entries";

        var plain = _gameGui.GetAddonByName("SelectString");
        if (!plain.IsNull && plain.IsVisible)
            return $"SelectString with {((AddonSelectString*)plain.Address)->PopupMenu.PopupMenu.EntryCount} entries";

        return null;
    }

    private void CloseScriptedMenu()
    {
        foreach (var name in new[] { "SelectIconString", "SelectString" })
        {
            var addon = _gameGui.GetAddonByName(name);
            if (!addon.IsNull && addon.IsVisible)
                ((AtkUnitBase*)addon.Address)->FireCallbackInt(-1); // the menu's own cancel
        }
    }

    public bool ShopOpen(uint shopId)
    {
        try
        {
            var agent = AgentShop.Instance();
            if (agent == null || !agent->IsAgentActive() || agent->EventReceiver == null || !agent->IsAddonReady())
                return false;
            if (!Handler(shopId, out var handler))
                return false;

            var proxy = (ShopEventHandler.AgentProxy*)agent->EventReceiver;
            return proxy->Handler == handler;
        }
        catch
        {
            return false;
        }
    }

    public bool ShopBusy(uint shopId)
    {
        try
        {
            return Handler(shopId, out var handler) && ((ShopEventHandler*)handler)->WaitingForTransactionToFinish;
        }
        catch
        {
            return false;
        }
    }

    public bool Buy(uint shopId, uint itemId, int count)
    {
        try
        {
            if (!Handler(shopId, out var handler) || handler->Info.EventId.ContentId != EventHandlerContent.Shop)
                return false;

            var shop = (ShopEventHandler*)handler;
            for (var i = 0; i < shop->VisibleItemsCount; i++)
            {
                var index = shop->VisibleItems[i];
                if (shop->Items[index].ItemId != itemId)
                    continue;

                shop->BuyItemIndex = index;
                shop->ExecuteBuy(count);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: buying {0} x item {1} failed", count, itemId);
            return false;
        }
    }

    public void CloseShop()
    {
        try
        {
            var agent = AgentShop.Instance();
            if (agent == null || agent->EventReceiver == null)
                return;

            var proxy = (ShopEventHandler.AgentProxy*)agent->EventReceiver;
            if (proxy->Handler != null)
                proxy->Handler->CancelInteraction();

            var result = default(AtkValue);
            var arg = default(AtkValue);
            arg.SetInt(-1);
            agent->ReceiveEvent(&result, &arg, 1, 0);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: closing the shop failed");
        }
    }

    private static bool Handler(uint shopId, out EventHandler* handler)
    {
        handler = null;
        var framework = EventFramework.Instance();
        if (framework == null)
            return false;

        var map = framework->EventHandlerModule.EventHandlerMap;
        if (!map.TryGetValuePointer(shopId, out var entry) || entry == null || entry->Value == null)
            return false;

        handler = entry->Value;
        return true;
    }

    private static GameObject* FindObject(uint dataId)
    {
        var objects = GameObjectManager.Instance();
        if (objects == null)
            return null;

        foreach (var obj in objects->Objects.IndexSorted)
        {
            if (obj.Value != null && obj.Value->BaseId == dataId)
                return obj.Value;
        }

        return null;
    }
}
