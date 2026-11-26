using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Materials;
using ENpcResident = Lumina.Excel.Sheets.ENpcResident;
using Item = Lumina.Excel.Sheets.Item;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace Hephaestus.Services.Game;

/// <summary>
/// "[H] Vendor Location ▸" on the game's right-click menus, for any item an NPC sells for gil (user,
/// 2026-11-26, after Item Vendor Location's entry of the same name): a submenu of sellers, one a zone
/// (<see cref="VendorMenu"/>); picking one opens the map with a flag on them and says the price and the place
/// in chat. It reads what the vendor trips read - <see cref="VendorCatalog"/> and <see cref="VendorLocations"/> -
/// so it knows GIL shops an NPC runs directly and nothing else: no scrips, seals or tomestones.
/// It only shows; nothing travels or buys from here. Which item was right-clicked: <see cref="QueueContextMenu"/>.
/// </summary>
public sealed class VendorContextMenu : IDisposable
{
    /// <summary>UIColor row for the prefix letter - the queue entry's.</summary>
    private const ushort PrefixColor = 706;

    private readonly IContextMenu _contextMenu;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _data;
    private readonly VendorCatalog _catalog;
    private readonly VendorLocations _locations;
    private readonly Func<bool> _enabled;
    private readonly Func<uint> _territory;
    private readonly Func<Vector3> _playerPosition;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;

    /// <param name="enabled">Logged in, and the menu entry switched on.</param>
    public VendorContextMenu(
        IContextMenu contextMenu, IGameGui gameGui, IDataManager data, VendorCatalog catalog, VendorLocations locations,
        Func<bool> enabled, Func<uint> territory, Func<Vector3> playerPosition, IChatGui chat, IPluginLog log)
    {
        _contextMenu = contextMenu;
        _gameGui = gameGui;
        _data = data;
        _catalog = catalog;
        _locations = locations;
        _enabled = enabled;
        _territory = territory;
        _playerPosition = playerPosition;
        _chat = chat;
        _log = log;

        _contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose() => _contextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        try
        {
            if (!_enabled())
                return;

            var itemId = QueueContextMenu.Resolve(args, _gameGui).ItemId;
            if (itemId == 0 || _catalog.PriceOf(itemId) == 0)
                return; // no vendor we can name sells it: stay out of the menu

            _locations.EnsureStarted(); // read on a background task the first time; the submenu says so until it is done
            args.AddMenuItem(new MenuItem
            {
                Name = "Vendor Location",
                PrefixChar = 'H',
                PrefixColor = PrefixColor,
                IsSubmenu = true,
                OnClicked = clicked => clicked.OpenSubmenu(SellerMenu(itemId)),
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hephaestus: vendor context menu failed"); // never break the game's own menu
        }
    }

    private List<MenuItem> SellerMenu(uint itemId)
    {
        if (!_locations.Ready)
            return Note("Reading vendor positions — try again in a moment");

        var placed = new List<PlacedSeller>();
        foreach (var vendor in _catalog.VendorsFor(itemId))
        {
            if (_locations.Find(vendor.NpcDataId) is { } at)
                placed.Add(new PlacedSeller(vendor.NpcDataId, at.TerritoryId, at.Position));
        }

        var picked = VendorMenu.Pick(placed, _territory(), _playerPosition());
        if (picked.Count == 0)
            return Note("Sold by a vendor, but the game data does not say where they stand");

        var items = new List<MenuItem>(picked.Count);
        foreach (var seller in picked)
        {
            var choice = seller;
            items.Add(new MenuItem
            {
                Name = $"{NpcName(choice.NpcDataId)} — {ZoneName(choice.Territory)}",
                PrefixChar = 'H',
                PrefixColor = PrefixColor,
                OnClicked = _ => Show(itemId, choice),
            });
        }

        return items;
    }

    private static List<MenuItem> Note(string text)
        => new() { new MenuItem { Name = text, PrefixChar = 'H', PrefixColor = PrefixColor, IsEnabled = false } };

    /// <summary>Flag the seller on the map, open it, and say who, where and for how much in chat.</summary>
    private void Show(uint itemId, PlacedSeller seller)
    {
        try
        {
            if (_data.GetExcelSheet<TerritoryType>().GetRowOrDefault(seller.Territory) is not { } territory)
                return;

            // Raw map-link coordinates are the world's, in thousandths.
            var rawX = (int)MathF.Round(seller.Position.X * 1000f);
            var rawY = (int)MathF.Round(seller.Position.Z * 1000f);
            var mapId = territory.Map.RowId;
            _gameGui.OpenMapWithMapLink(new MapLinkPayload(seller.Territory, mapId, rawX, rawY));

            var item = _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"item {itemId}";
            _chat.Print(new SeStringBuilder()
                .AddText($"[Hephaestus] {item}: {_catalog.PriceOf(itemId):N0} gil from {NpcName(seller.NpcDataId)}, ")
                .Append(SeString.CreateMapLink(seller.Territory, mapId, rawX, rawY))
                .Build());
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hephaestus: showing a vendor's place failed");
        }
    }

    private string NpcName(uint npcDataId)
    {
        var name = _data.GetExcelSheet<ENpcResident>().GetRowOrDefault(npcDataId)?.Singular.ExtractText();
        return string.IsNullOrEmpty(name) ? "a vendor" : name;
    }

    private string ZoneName(uint territoryId)
        => _data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? $"zone {territoryId}";
}
