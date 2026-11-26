using System;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Hephaestus.Features.Materials;
using Lumina.Text.ReadOnly;
using ENpcResident = Lumina.Excel.Sheets.ENpcResident;
using Item = Lumina.Excel.Sheets.Item;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace Hephaestus.Services.Game;

/// <summary>
/// Showing a person the way to a seller (user, 2026-11-26: "as soon as the location is picked it highlights the
/// merchant and item/submenu" - what Item Vendor Location does, written here from scratch): the map opens with a
/// flag on them and the place goes to chat; from then on the MERCHANT is outlined in the world, their shop's line
/// is gold in the menu an NPC with several purposes puts up, and the ITEM is gold in the shop's list. Until
/// <see cref="Clear"/> (the window closing). It only shows - nothing is clicked, bought or walked to here.
/// <para>
/// The merchant is the game's own outline (<c>GameObject.Highlight</c>), put back every 300 ms. The menu line is
/// found by the event selector's own option, as <see cref="VendorWorld"/> picks a shop - typed, not by its text.
/// The shop's list is matched by the item's NAME (its rows are reused as it scrolls, so a row number means
/// nothing): the <c>Shop</c> addon's list is node 16 and a row's name node 3; a special merchant's windows
/// (<c>ShopExchangeItem</c>, <c>ShopExchangeCurrency</c>, <c>InclusionShop</c>) are tree lists with their own
/// numbers (<see cref="ExchangeLists"/>) - all Item Vendor Location's numbers, NOT seen in game from here. A Grand
/// Company's exchange window is not lit: nobody has its numbers. A gold row stays gold until the game next fills it.
/// </para>
/// </summary>
public sealed unsafe class VendorGuide
{
    private const uint ShopListNode = 16;
    private const uint ShopRowNameNode = 3;

    /// <summary>A special merchant's window: the tree list's node (one of these), and a row's name node (one of these).</summary>
    private static readonly (string Addon, uint[] ListNodes, uint[] NameNodes)[] ExchangeLists =
    {
        ("ShopExchangeItem", new uint[] { 20 }, new uint[] { 7 }),
        ("ShopExchangeCurrency", new uint[] { 19, 20 }, new uint[] { 3, 8 }),
        ("InclusionShop", new uint[] { 19 }, new uint[] { 5 }),
    };

    private static readonly TimeSpan OutlineEvery = TimeSpan.FromMilliseconds(300);
    private static readonly ByteColor Gold = new() { R = 217, G = 166, B = 51, A = 255 }; // HephaestusTheme.AccentGold

    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objects;
    private readonly IDataManager _data;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    private uint _itemId;
    private string _itemName = string.Empty;
    private uint _npcDataId;
    private uint _shopId;
    private DateTime _outlinedAt;

    public VendorGuide(IGameGui gameGui, IObjectTable objects, IDataManager data, IChatGui chat, IPluginLog log)
    {
        _gameGui = gameGui;
        _objects = objects;
        _data = data;
        _chat = chat;
        _log = log;
    }

    /// <summary>What is being shown, or why nothing is.</summary>
    public string Status { get; private set; } = "Idle — no seller picked.";

    /// <summary>The seller being shown; 0 when none.</summary>
    public uint ShownNpc => _npcDataId;

    /// <summary>Flag the seller on the map, open it, say who, where and for how much in chat - and keep them and the item lit.</summary>
    /// <param name="cost">What they ask, in words: "4,620 gil", "600 × Storm Seal".</param>
    public void Show(uint itemId, PlacedSeller seller, string cost)
    {
        try
        {
            Clear();
            _itemId = itemId;
            _itemName = _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;
            _npcDataId = seller.NpcDataId;
            _shopId = seller.ShopId;
            Status = $"Showing {NpcName(seller.NpcDataId)} for {_itemName}";

            if (seller.Territory == 0 || _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(seller.Territory) is not { } territory)
                return;

            // Raw map-link coordinates are the world's, in thousandths.
            var rawX = (int)MathF.Round(seller.Position.X * 1000f);
            var rawY = (int)MathF.Round(seller.Position.Z * 1000f);
            var mapId = territory.Map.RowId;
            _gameGui.OpenMapWithMapLink(new MapLinkPayload(seller.Territory, mapId, rawX, rawY));
            _chat.Print(new SeStringBuilder()
                .AddText($"[Hephaestus] {_itemName}: {cost} from {NpcName(seller.NpcDataId)}, ")
                .Append(SeString.CreateMapLink(seller.Territory, mapId, rawX, rawY))
                .Build());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hephaestus: showing a vendor's place failed");
        }
    }

    /// <summary>Stop showing: the merchant's outline is taken off; nothing more is coloured.</summary>
    public void Clear()
    {
        if (_npcDataId == 0)
            return;

        try
        {
            Outline(ObjectHighlightColor.None);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: taking a merchant's outline off failed");
        }

        _itemId = 0;
        _itemName = string.Empty;
        _npcDataId = 0;
        _shopId = 0;
        Status = "Idle — no seller picked.";
    }

    /// <summary>Framework thread, every frame: cheap when nothing is shown.</summary>
    public void Tick(DateTime now)
    {
        if (_npcDataId == 0)
            return;

        if (now - _outlinedAt >= OutlineEvery)
        {
            _outlinedAt = now;
            Outline(ObjectHighlightColor.Yellow);
        }

        LightShopLine();
        LightItemRow();
        LightExchangeRow();
    }

    private void Outline(ObjectHighlightColor color)
    {
        foreach (var obj in _objects)
        {
            if (obj == null || obj.Address == nint.Zero)
                continue;

            var gameObject = (GameObject*)obj.Address;
            if (gameObject->BaseId == _npcDataId && gameObject->ObjectKind == ObjectKind.EventNpc)
                gameObject->Highlight(color);
        }
    }

    /// <summary>The menu of an NPC with several purposes: the line that opens the seller's shop.</summary>
    private void LightShopLine()
    {
        var selector = EventHandlerSelector.Instance();
        if (selector == null || selector->Target == null || selector->Target->BaseId != _npcDataId)
            return;

        var line = -1;
        for (var i = 0; i < selector->OptionsCount; i++)
        {
            var handler = selector->Options[i].Handler;
            if (handler != null && handler->Info.EventId.Id == _shopId)
            {
                line = i;
                break;
            }
        }

        if (line < 0)
            return;

        var icon = _gameGui.GetAddonByName("SelectIconString");
        if (!icon.IsNull && icon.IsVisible)
            Light(((AddonSelectIconString*)icon.Address)->PopupMenu.PopupMenu.List, line);

        var plain = _gameGui.GetAddonByName("SelectString");
        if (!plain.IsNull && plain.IsVisible)
            Light(((AddonSelectString*)plain.Address)->PopupMenu.PopupMenu.List, line);
    }

    private static void Light(AtkComponentList* list, int line)
    {
        if (list == null || line >= list->ListLength)
            return;

        var row = list->ItemRendererList[line].AtkComponentListItemRenderer;
        if (row == null)
            return;

        var text = row->AtkComponentButton.ButtonTextNode;
        if (text != null)
            text->TextColor = Gold;
    }

    /// <summary>The shop's own list: the row that names the item.</summary>
    private void LightItemRow()
    {
        if (_itemName.Length == 0)
            return;

        var shop = _gameGui.GetAddonByName("Shop");
        if (shop.IsNull || !shop.IsVisible)
            return;

        var component = ((AtkUnitBase*)shop.Address)->GetComponentByNodeId(ShopListNode);
        if (component == null)
            return;

        var list = (AtkComponentList*)component;
        for (var i = 0; i < list->ListLength; i++)
        {
            var row = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (row == null)
                continue;

            var text = row->AtkComponentButton.AtkComponentBase.GetTextNodeById(ShopRowNameNode);
            if (text == null)
                continue;

            var shown = new ReadOnlySeStringSpan(text->NodeText.AsSpan()).ExtractText();
            if (!VendorMenu.RowNames(shown, _itemName))
                continue;

            if (text->TextColor.R != Gold.R || text->TextColor.G != Gold.G || text->TextColor.B != Gold.B)
            {
                text->TextColor = Gold;
                text->SetText(shown); // the list only repaints a row's colour when its text is set
            }

            return;
        }
    }

    /// <summary>A special merchant's window: the row that names the item.</summary>
    private void LightExchangeRow()
    {
        if (_itemName.Length == 0)
            return;

        foreach (var (name, listNodes, nameNodes) in ExchangeLists)
        {
            var addon = _gameGui.GetAddonByName(name);
            if (addon.IsNull || !addon.IsVisible)
                continue;

            AtkComponentBase* component = null;
            foreach (var node in listNodes)
            {
                component = ((AtkUnitBase*)addon.Address)->GetComponentByNodeId(node);
                if (component != null)
                    break;
            }

            if (component == null)
                continue;

            foreach (var item in ((AtkComponentTreeList*)component)->Items)
            {
                if (item.Value == null || item.Value->Renderer == null)
                    continue;

                AtkTextNode* text = null;
                foreach (var node in nameNodes)
                {
                    text = item.Value->Renderer->AtkComponentButton.AtkComponentBase.GetTextNodeById(node);
                    if (text != null)
                        break;
                }

                if (text == null)
                    continue;

                var shown = new ReadOnlySeStringSpan(text->NodeText.AsSpan()).ExtractText();
                if (!VendorMenu.RowNames(shown, _itemName))
                    continue;

                if (text->TextColor.R != Gold.R || text->TextColor.G != Gold.G || text->TextColor.B != Gold.B)
                {
                    text->TextColor = Gold;
                    text->SetText(shown); // as in the gil shop: the colour only shows once the text is set
                }

                break;
            }
        }
    }

    private string NpcName(uint npcDataId)
    {
        var name = _data.GetExcelSheet<ENpcResident>().GetRowOrDefault(npcDataId)?.Singular.ExtractText();
        return string.IsNullOrEmpty(name) ? "a vendor" : name;
    }
}
