using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace Hephaestus.Services.Game;

/// <summary>
/// "[H] Vendor Location" on the game's right-click menus, for any item an NPC sells (user, 2026-11-26, after
/// Item Vendor Location's entry of the same name): it opens the Vendor Location window
/// (<c>Windows/VendorLocationWindow</c>) on that item. Gil vendors come from what the vendor trips read -
/// <see cref="VendorCatalog"/>; special merchants (seals, scrips, tomestones, an item for an item) from
/// <see cref="SpecialVendorCatalog"/>, which is read in the background from the first menu that opens - so for a
/// second or so after that an item only a special merchant sells has no entry yet.
/// Which item was right-clicked: <see cref="QueueContextMenu"/>.
/// </summary>
public sealed class VendorContextMenu : IDisposable
{
    /// <summary>UIColor row for the prefix letter - the queue entry's.</summary>
    private const ushort PrefixColor = 706;

    private readonly IContextMenu _contextMenu;
    private readonly IGameGui _gameGui;
    private readonly VendorCatalog _catalog;
    private readonly SpecialVendorCatalog _special;
    private readonly Func<bool> _enabled;
    private readonly Action<uint> _open;
    private readonly IPluginLog _log;

    /// <param name="enabled">Logged in, and the menu entry switched on.</param>
    /// <param name="open">Open the Vendor Location window on an item.</param>
    public VendorContextMenu(
        IContextMenu contextMenu, IGameGui gameGui, VendorCatalog catalog, SpecialVendorCatalog special,
        Func<bool> enabled, Action<uint> open, IPluginLog log)
    {
        _contextMenu = contextMenu;
        _gameGui = gameGui;
        _catalog = catalog;
        _special = special;
        _enabled = enabled;
        _open = open;
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

            _special.EnsureStarted();
            var itemId = QueueContextMenu.Resolve(args, _gameGui).ItemId;
            if (itemId == 0 || (_catalog.PriceOf(itemId) == 0 && _special.SellersOf(itemId).Count == 0))
                return; // nobody we can name sells it: stay out of the menu

            args.AddMenuItem(new MenuItem
            {
                Name = "Vendor Location",
                PrefixChar = 'H',
                PrefixColor = PrefixColor,
                OnClicked = _ => _open(itemId),
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hephaestus: vendor context menu failed"); // never break the game's own menu
        }
    }

    // [SUPERSEDED 2026-11-26 by Windows/VendorLocationWindow — the entry was a submenu of sellers, one a zone; the user
    // asked for a window like Item Vendor Location's. Flagging the map and the chat line moved to VendorGuide.Show]
    // private List<MenuItem> SellerMenu(uint itemId)
    // {
    //     if (!_locations.Ready)
    //         return Note("Reading vendor positions — try again in a moment");
    //
    //     var placed = new List<PlacedSeller>();
    //     foreach (var vendor in _catalog.VendorsFor(itemId))
    //     {
    //         if (_locations.Find(vendor.NpcDataId) is { } at)
    //             placed.Add(new PlacedSeller(vendor.NpcDataId, at.TerritoryId, at.Position));
    //     }
    //
    //     var picked = VendorMenu.Pick(placed, _territory(), _playerPosition());
    //     if (picked.Count == 0)
    //         return Note("Sold by a vendor, but the game data does not say where they stand");
    //
    //     var items = new List<MenuItem>(picked.Count);
    //     foreach (var seller in picked)
    //     {
    //         var choice = seller;
    //         items.Add(new MenuItem
    //         {
    //             Name = $"{NpcName(choice.NpcDataId)} — {ZoneName(choice.Territory)}",
    //             PrefixChar = 'H',
    //             PrefixColor = PrefixColor,
    //             OnClicked = _ => Show(itemId, choice),
    //         });
    //     }
    //
    //     return items;
    // }
    //
    // private static List<MenuItem> Note(string text)
    //     => new() { new MenuItem { Name = text, PrefixChar = 'H', PrefixColor = PrefixColor, IsEnabled = false } };
}
