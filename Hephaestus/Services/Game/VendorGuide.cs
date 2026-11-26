using System;
using System.Collections.Generic;
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
/// The item is found by its NAME (a list's rows are reused as it scrolls, so a row number means nothing), by
/// walking every node of whichever shop window is up (<see cref="ShopWindows"/>) - no node numbers. A gold row
/// stays gold until the game next fills it. SEEN IN GAME (user, 2026-11-26): the outline, on a quartermaster -
/// yellow first, then red as asked - and the chat line.
/// </para>
/// <para>
/// A LIST'S ROWS ARE NOT IN THE WINDOW'S NODE TREE (Charon's WindowTextDump found it the hard way: a window showing
/// eleven rows reported four lines of text): they are item renderers, components of their own, reached only by
/// asking the list for them. The first walk here followed the tree alone and left the user's Duck Bones dark in the
/// Grand Company's exchange. <see cref="VisitComponent"/> asks a List / TreeList for its rows - and asks the
/// COMPONENT what it is first, never the node's type: those calls are virtual, and on anything else they crash the
/// game. What each look found is in <see cref="Status"/> and, when it changes, in the plugin log.
/// </para>
/// </summary>
public sealed unsafe class VendorGuide
{
    /// <summary>The windows a seller's stock is listed in. A name the game does not have costs nothing.</summary>
    private static readonly string[] ShopWindows =
    {
        "Shop", "ShopExchangeItem", "ShopExchangeCurrency", "InclusionShop", "GrandCompanyExchange", "FreeCompanyCreditShop",
    };

    // [SUPERSEDED 2026-11-26 by AtkResNode.GetAsAtkComponentNode — the SDK's own cast, as Charon's WindowTextDump uses]
    // /// <summary>Node types from here up are components, with nodes of their own.</summary>
    // private const ushort ComponentNodeType = 1000;

    /// <summary>A window is a few hundred nodes; the caps are against a tree that loops.</summary>
    private const int NodeBudget = 6000;
    private const int MaxDepth = 32;

    /// <summary>A list is asked for this many rows at most.</summary>
    private const int MaxRows = 400;

    /// <summary>An item's name, less whatever the game wrapped it in, fits this many bytes; a longer line is not one.</summary>
    private const int PlainMax = 192;

    /// <summary>What a look found is logged when it changes - this many times a seller shown, no more.</summary>
    private const int MaxReports = 12;

    // [SUPERSEDED 2026-11-26 by ShopWindows — see LightItemRows]
    // private const uint ShopListNode = 16;
    // private const uint ShopRowNameNode = 3;
    //
    // /// <summary>A special merchant's window: the tree list's node (one of these), and a row's name node (one of these).</summary>
    // private static readonly (string Addon, uint[] ListNodes, uint[] NameNodes)[] ExchangeLists =
    // {
    //     ("ShopExchangeItem", new uint[] { 20 }, new uint[] { 7 }),
    //     ("ShopExchangeCurrency", new uint[] { 19, 20 }, new uint[] { 3, 8 }),
    //     ("InclusionShop", new uint[] { 19 }, new uint[] { 5 }),
    // };

    private static readonly TimeSpan OutlineEvery = TimeSpan.FromMilliseconds(300);
    private static readonly ByteColor Gold = new() { R = 217, G = 166, B = 51, A = 255 }; // HephaestusTheme.AccentGold

    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objects;
    private readonly IDataManager _data;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    private uint _itemId;
    private string _itemName = string.Empty;
    private byte[] _itemNameUtf8 = Array.Empty<byte>();
    private byte[] _itemFirstWordUtf8 = Array.Empty<byte>();
    private int _nodeBudget;

    // One look at one window: nodes reached, lines of text among them, list rows asked for, lines naming the item.
    private readonly HashSet<nint> _seen = new();
    private readonly List<string> _near = new();
    private int _texts;
    private int _rows;
    private int _named;
    private int _painted; // since the seller was shown: climbing while a window is up = the game repaints the row
    private bool _reporting;
    private int _reports;
    private string _showing = string.Empty;
    private string _lastReport = string.Empty;
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
            _itemNameUtf8 = System.Text.Encoding.UTF8.GetBytes(_itemName);
            var space = _itemName.IndexOf(' ');
            _itemFirstWordUtf8 = System.Text.Encoding.UTF8.GetBytes(space > 0 ? _itemName[..space] : _itemName);
            _painted = 0;
            _reports = 0;
            _lastReport = string.Empty;
            _showing = $"Showing {NpcName(seller.NpcDataId)} for {_itemName}";
            Status = _showing;

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
        _itemNameUtf8 = Array.Empty<byte>();
        _itemFirstWordUtf8 = Array.Empty<byte>();
        _npcDataId = 0;
        _shopId = 0;
        Status = "Idle — no seller picked.";
    }

    /// <summary>Framework thread, every frame: cheap when nothing is shown.</summary>
    public void Tick(DateTime now)
    {
        if (_npcDataId == 0)
            return;

        // What a look finds is put into words on the outline's beat only: every frame would be a string a frame.
        _reporting = now - _outlinedAt >= OutlineEvery;
        if (_reporting)
        {
            _outlinedAt = now;
            Outline(ObjectHighlightColor.Red);
            Status = _showing;
        }

        LightShopLine();
        LightItemRows();
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

    /// <summary>
    /// Every shop window that is up: any line of text that names the item turns gold. Found by walking the
    /// window's nodes - no node numbers, so a window nobody mapped (the Grand Company's exchange) is lit as well.
    /// </summary>
    private void LightItemRows()
    {
        if (_itemNameUtf8.Length == 0)
            return;

        foreach (var name in ShopWindows)
        {
            var addon = _gameGui.GetAddonByName(name);
            if (addon.IsNull || !addon.IsVisible)
                continue;

            var unit = (AtkUnitBase*)addon.Address;
            _seen.Clear();
            _near.Clear();
            _nodeBudget = NodeBudget;
            _texts = 0;
            _rows = 0;
            _named = 0;
            Visit(unit->RootNode, 0);
            VisitList(&unit->UldManager, 0);

            if (_reporting)
                Report(name);
        }
    }

    /// <summary>A node, its later siblings and everything under them - a component's own nodes and a list's rows included.</summary>
    private void Visit(AtkResNode* node, int depth)
    {
        // The budget counts steps, seen or not: a sibling chain that loops ends on it.
        for (; node != null && depth <= MaxDepth && _nodeBudget-- > 0; node = node->PrevSiblingNode)
        {
            if (!_seen.Add((nint)node))
                continue;

            // The SDK's own casts, not a comparison against the type by hand.
            var text = node->GetAsAtkTextNode();
            if (text != null)
            {
                Light(text);
            }
            else
            {
                var owner = node->GetAsAtkComponentNode();
                if (owner != null && owner->Component != null)
                    VisitComponent(owner->Component, depth + 1);
            }

            if (node->ChildNode != null)
                Visit(node->ChildNode, depth + 1);
        }
    }

    /// <summary>A component keeps its content to itself: its own tree, its own node list - and, a list, its rows.</summary>
    private void VisitComponent(AtkComponentBase* component, int depth)
    {
        if (depth > MaxDepth || !_seen.Add((nint)component))
            return;

        Visit(component->UldManager.RootNode, depth);
        VisitList(&component->UldManager, depth);

        // Asked of the COMPONENT: GetItemCount / GetItemRenderer are virtual, and on anything that is not a list
        // they are a call into nowhere - a crash nothing can catch.
        var kind = component->GetComponentType();
        if (kind != ComponentType.List && kind != ComponentType.TreeList)
            return;

        var list = (AtkComponentList*)component;
        var rows = Math.Min(list->GetItemCount(), MaxRows);
        for (var i = 0; i < rows && _nodeBudget > 0; i++)
        {
            // Null for a row that is scrolled out of sight: the list keeps only as many as it shows.
            var row = list->GetItemRenderer(i);
            if (row == null)
                continue;

            _rows++;
            VisitComponent((AtkComponentBase*)row, depth + 1);
        }
    }

    /// <summary>The flat list a window or component keeps beside its tree: a node can be on either. Slot 0 is not a node.</summary>
    private void VisitList(AtkUldManager* manager, int depth)
    {
        if (manager->NodeList == null)
            return;

        for (var i = 1; i < manager->NodeListCount && _nodeBudget > 0; i++)
        {
            var node = manager->NodeList[i];
            if (node != null && !_seen.Contains((nint)node))
                Visit(node, depth + 1);
        }
    }

    private void Light(AtkTextNode* text)
    {
        var raw = text->NodeText.AsSpan();
        if (raw.IsEmpty)
            return;

        _texts++;

        // A name the game wrapped in something (a colour, a link) is the text between the wrappings.
        Span<byte> plain = stackalloc byte[PlainMax];
        scoped ReadOnlySpan<byte> shown = raw;
        if (raw.IndexOf((byte)2) >= 0)
        {
            var length = 0;
            foreach (var payload in new ReadOnlySeStringSpan(raw))
            {
                if (payload.Type != ReadOnlySePayloadType.Text)
                    continue;
                if (length + payload.Body.Length > plain.Length)
                    return;

                payload.Body.CopyTo(plain[length..]);
                length += payload.Body.Length;
            }

            shown = plain[..length];
        }

        if (!VendorMenu.RowNames(shown, _itemNameUtf8))
        {
            // For the log: a line that begins as the item does and was not taken for it.
            if (_reporting && _near.Count < 4 && _itemFirstWordUtf8.Length > 0 && shown.IndexOf(_itemFirstWordUtf8) >= 0)
                _near.Add($"\"{System.Text.Encoding.UTF8.GetString(shown)}\" ({Convert.ToHexString(raw[..Math.Min(raw.Length, 48)])})");
            return;
        }

        _named++;
        if (text->TextColor.R == Gold.R && text->TextColor.G == Gold.G && text->TextColor.B == Gold.B)
            return;

        _painted++;
        text->TextColor = Gold;
        text->SetText(System.Text.Encoding.UTF8.GetString(shown)); // a list only repaints a row's colour when its text is set
    }

    /// <summary>What this look at a window found - in <see cref="Status"/>, and in the plugin log when it is news.</summary>
    private void Report(string window)
    {
        var line = $"{window}: {_seen.Count} nodes, {_rows} list rows, {_texts} lines of text, {_named} naming {_itemName}, painted {_painted}x";
        if (_near.Count > 0)
            line += "; near: " + string.Join(" | ", _near);

        Status = $"{_showing} — {line}";
        if (_reports >= MaxReports || string.Equals(line, _lastReport, StringComparison.Ordinal))
            return;

        _reports++;
        _lastReport = line;
        _log.Information("Hephaestus vendor guide: {Line}", line);
    }

    // [SUPERSEDED 2026-11-26 by the walk above — this one followed the node tree alone; a list's rows are not on it
    // (item renderers, asked of the list), so nothing in the Grand Company's exchange was lit]
    // /// <summary>
    // /// Every shop window that is up: any line of text that names the item turns gold. Found by walking the
    // /// window's nodes - no node numbers, so a window nobody mapped (the Grand Company's exchange) is lit as well.
    // /// </summary>
    // private void LightItemRows()
    // {
    //     if (_itemNameUtf8.Length == 0)
    //         return;
    //
    //     foreach (var name in ShopWindows)
    //     {
    //         var addon = _gameGui.GetAddonByName(name);
    //         if (addon.IsNull || !addon.IsVisible)
    //             continue;
    //
    //         _nodeBudget = NodeBudget;
    //         Visit(((AtkUnitBase*)addon.Address)->RootNode, 0);
    //     }
    // }
    //
    // /// <summary>A node, its later siblings and everything under them - a component's own nodes included.</summary>
    // private void Visit(AtkResNode* node, int depth)
    // {
    //     for (; node != null && _nodeBudget-- > 0; node = node->PrevSiblingNode)
    //     {
    //         if (node->Type == NodeType.Text)
    //         {
    //             Light((AtkTextNode*)node);
    //         }
    //         else if ((ushort)node->Type >= ComponentNodeType && depth < MaxDepth)
    //         {
    //             var component = ((AtkComponentNode*)node)->Component;
    //             if (component != null)
    //                 Visit(component->UldManager.RootNode, depth + 1);
    //         }
    //
    //         if (node->ChildNode != null && depth < MaxDepth)
    //             Visit(node->ChildNode, depth + 1);
    //     }
    // }
    //
    // private void Light(AtkTextNode* text)
    // {
    //     if (!VendorMenu.RowNames(text->NodeText.AsSpan(), _itemNameUtf8))
    //         return;
    //     if (text->TextColor.R == Gold.R && text->TextColor.G == Gold.G && text->TextColor.B == Gold.B)
    //         return;
    //
    //     var shown = new ReadOnlySeStringSpan(text->NodeText.AsSpan()).ExtractText();
    //     text->TextColor = Gold;
    //     text->SetText(shown); // a list only repaints a row's colour when its text is set
    // }

    // [SUPERSEDED 2026-11-26 by LightItemRows — rows were found by node NUMBERS (Item Vendor Location's), one set a
    // window, and the Grand Company's exchange had none: the user's Duck Bones stayed unlit there]
    // /// <summary>The shop's own list: the row that names the item.</summary>
    // private void LightItemRow()
    // {
    //     if (_itemName.Length == 0)
    //         return;
    //
    //     var shop = _gameGui.GetAddonByName("Shop");
    //     if (shop.IsNull || !shop.IsVisible)
    //         return;
    //
    //     var component = ((AtkUnitBase*)shop.Address)->GetComponentByNodeId(ShopListNode);
    //     if (component == null)
    //         return;
    //
    //     var list = (AtkComponentList*)component;
    //     for (var i = 0; i < list->ListLength; i++)
    //     {
    //         var row = list->ItemRendererList[i].AtkComponentListItemRenderer;
    //         if (row == null)
    //             continue;
    //
    //         var text = row->AtkComponentButton.AtkComponentBase.GetTextNodeById(ShopRowNameNode);
    //         if (text == null)
    //             continue;
    //
    //         var shown = new ReadOnlySeStringSpan(text->NodeText.AsSpan()).ExtractText();
    //         if (!VendorMenu.RowNames(shown, _itemName))
    //             continue;
    //
    //         if (text->TextColor.R != Gold.R || text->TextColor.G != Gold.G || text->TextColor.B != Gold.B)
    //         {
    //             text->TextColor = Gold;
    //             text->SetText(shown); // the list only repaints a row's colour when its text is set
    //         }
    //
    //         return;
    //     }
    // }
    //
    // /// <summary>A special merchant's window: the row that names the item.</summary>
    // private void LightExchangeRow()
    // {
    //     if (_itemName.Length == 0)
    //         return;
    //
    //     foreach (var (name, listNodes, nameNodes) in ExchangeLists)
    //     {
    //         var addon = _gameGui.GetAddonByName(name);
    //         if (addon.IsNull || !addon.IsVisible)
    //             continue;
    //
    //         AtkComponentBase* component = null;
    //         foreach (var node in listNodes)
    //         {
    //             component = ((AtkUnitBase*)addon.Address)->GetComponentByNodeId(node);
    //             if (component != null)
    //                 break;
    //         }
    //
    //         if (component == null)
    //             continue;
    //
    //         foreach (var item in ((AtkComponentTreeList*)component)->Items)
    //         {
    //             if (item.Value == null || item.Value->Renderer == null)
    //                 continue;
    //
    //             AtkTextNode* text = null;
    //             foreach (var node in nameNodes)
    //             {
    //                 text = item.Value->Renderer->AtkComponentButton.AtkComponentBase.GetTextNodeById(node);
    //                 if (text != null)
    //                     break;
    //             }
    //
    //             if (text == null)
    //                 continue;
    //
    //             var shown = new ReadOnlySeStringSpan(text->NodeText.AsSpan()).ExtractText();
    //             if (!VendorMenu.RowNames(shown, _itemName))
    //                 continue;
    //
    //             if (text->TextColor.R != Gold.R || text->TextColor.G != Gold.G || text->TextColor.B != Gold.B)
    //             {
    //                 text->TextColor = Gold;
    //                 text->SetText(shown); // as in the gil shop: the colour only shows once the text is set
    //             }
    //
    //             break;
    //         }
    //     }
    // }

    private string NpcName(uint npcDataId)
    {
        var name = _data.GetExcelSheet<ENpcResident>().GetRowOrDefault(npcDataId)?.Singular.ExtractText();
        return string.IsNullOrEmpty(name) ? "a vendor" : name;
    }
}
