using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Hephaestus.Features.Materials;
using Hephaestus.Services.Game;
using Hephaestus.Windows.Components;
using ENpcResident = Lumina.Excel.Sheets.ENpcResident;
using Item = Lumina.Excel.Sheets.Item;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace Hephaestus.Windows;

/// <summary>
/// "Vendor Location": every NPC that sells an item - who, where, what it costs (user, 2026-11-26, after
/// Item Vendor Location's window; drawn the way the main window's pages are - a page header, then a card a seller,
/// the user: "make the ui look similiar to hephaestus ui"). Opened on an item by the right-click entry. Show hands
/// the seller to <see cref="VendorGuide"/>, which flags the map and keeps the merchant and the item lit until this
/// window closes. Sellers in the character's zone come first, nearest first (<see cref="VendorMenu.Order"/>).
/// <para>
/// GIL vendors (<see cref="VendorCatalog"/>, what the queue buys from) and SPECIAL merchants
/// (<see cref="SpecialVendorCatalog"/> - seals, scrips, tomestones, an item for an item; the user: "can we do special
/// merchants also?") in one list. A Grand Company's quartermaster is listed only for the character's own company.
/// Sellers the game data does not place (housing vendors, mostly - forty-five "material suppliers" sell Iron Ore)
/// are one line a name, with how many.
/// </para>
/// </summary>
public sealed class VendorLocationWindow : Window
{
    /// <param name="Count">How many unplaced sellers of that name and cost this one line stands for.</param>
    private sealed record Row(PlacedSeller Seller, string Npc, string Place, string Cost, int Count = 1);

    private readonly IDataManager _data;
    private readonly VendorCatalog _catalog;
    private readonly SpecialVendorCatalog _special;
    private readonly VendorLocations _locations;
    private readonly VendorGuide _guide;
    private readonly Func<uint> _territory;
    private readonly Func<Vector3> _playerPosition;
    private readonly Func<byte> _grandCompany;

    private uint _itemId;
    private string _itemName = string.Empty;
    private List<Row>? _rows;
    private bool _rowsHaveSpecial;

    /// <param name="grandCompany">The character's Grand Company, 1-3; 0 when none or unknown.</param>
    public VendorLocationWindow(
        IDataManager data, VendorCatalog catalog, SpecialVendorCatalog special, VendorLocations locations, VendorGuide guide,
        Func<uint> territory, Func<Vector3> playerPosition, Func<byte> grandCompany)
        : base("Hephaestus — Vendor Location##HephaestusVendorLocation")
    {
        _data = data;
        _catalog = catalog;
        _special = special;
        _locations = locations;
        _guide = guide;
        _territory = territory;
        _playerPosition = playerPosition;
        _grandCompany = grandCompany;

        Size = new Vector2(520, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        IsOpen = false;
    }

    /// <summary>Open on an item (the right-click entry). Whoever was being shown before is let go.</summary>
    public void Show(uint itemId)
    {
        _guide.Clear();
        _itemId = itemId;
        _itemName = _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"item {itemId}";
        _rows = null;
        // Both are read on a background task the first time; the window says so until they are done.
        _locations.EnsureStarted();
        _special.EnsureStarted();
        IsOpen = true;
        BringToFront();
    }

    public override void OnClose() => _guide.Clear();

    public override void Draw()
    {
        using var style = HephaestusTheme.PushWindowStyle();

        if (!_locations.Ready)
        {
            MainWindow.PageHeader(_itemName, "Reading vendor positions from the game data…");
            return;
        }

        // The special merchants may land a moment after the window opened: list them when they do.
        if (_rows == null || (_special.Ready && !_rowsHaveSpecial))
        {
            _rowsHaveSpecial = _special.Ready;
            _rows = BuildRows();
        }

        var reading = _special.Ready ? string.Empty : " Still reading the special merchants…";
        MainWindow.PageHeader(_itemName, _rows.Count switch
        {
            0 => "No vendor sells this." + reading,
            1 => "Sold by one vendor. Show them and the map flags the spot; the merchant and the item in their shop stay lit until this window is closed." + reading,
            _ => $"Sold by {_rows.Count} vendors. Show one and the map flags the spot; the merchant and the item in their shop stay lit until this window is closed." + reading,
        });
        if (_rows.Count == 0)
            return;

        HephaestusTheme.SectionLabel("Sellers");
        HephaestusTheme.VSpace(2f);

        using var list = ImRaii.Child("##hephaestus_vendors", new Vector2(-1, -1), false);
        if (!list.Success)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var here = _territory();
        foreach (var row in _rows)
        {
            var seller = row.Seller;
            var shown = seller.Territory != 0 && _guide.ShownNpc == seller.NpcDataId;
            var accent = shown ? HephaestusTheme.AccentGold
                : seller.Territory == 0 ? HephaestusTheme.BorderDim
                : seller.Territory == here ? HephaestusTheme.StatusGreen
                : HephaestusTheme.BorderDim;

            var origin = Card.BeginFlat();
            var width = ImGui.GetContentRegionAvail().X;
            using var id = ImRaii.PushId($"{seller.NpcDataId}_{seller.ShopId}"); // an armorer and a blacksmith can stand on one spot

            HephaestusTheme.Text((shown ? "> " : "") + row.Npc, shown ? HephaestusTheme.AccentGold : HephaestusTheme.TextPrimary);
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(row.Cost).X - 12f * scale);
            HephaestusTheme.Text(row.Cost, HephaestusTheme.TextSecondary);

            HephaestusTheme.Text(row.Place, seller.Territory == 0 ? HephaestusTheme.TextMuted : HephaestusTheme.TextSecondary);
            if (seller.Territory != 0)
            {
                var label = shown ? "Stop showing" : "Show";
                ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(label).X - 20f * scale);
                if (MainWindow.GhostButton(label))
                {
                    if (shown)
                        _guide.Clear();
                    else
                        _guide.Show(_itemId, seller, row.Cost);
                }
            }

            Card.EndFlat(origin, width, accent: accent);
        }
    }

    private List<Row> BuildRows()
    {
        // What each (NPC, shop) asks. An NPC can list one item twice, at two prices: both are said.
        var costs = new Dictionary<(uint Npc, uint Shop), string>();
        var sellers = new List<PlacedSeller>();

        void Add(uint npc, uint shop, string cost)
        {
            if (costs.TryGetValue((npc, shop), out var known))
            {
                if (!known.Contains(cost, StringComparison.Ordinal))
                    costs[(npc, shop)] = $"{known} or {cost}";
                return;
            }

            costs[(npc, shop)] = cost;
            sellers.Add(_locations.Find(npc) is { } at
                ? new PlacedSeller(npc, at.TerritoryId, at.Position, shop)
                : new PlacedSeller(npc, 0, Vector3.Zero, shop));
        }

        var price = _catalog.PriceOf(_itemId);
        foreach (var vendor in _catalog.VendorsFor(_itemId))
            Add(vendor.NpcDataId, vendor.ShopId, $"{price:N0} gil");

        var company = _grandCompany();
        foreach (var seller in _special.SellersOf(_itemId))
        {
            // A quartermaster serves their own company only.
            if (seller.GrandCompany != 0 && company != 0 && seller.GrandCompany != company)
                continue;

            Add(seller.NpcDataId, seller.ShopId, seller.Cost);
        }

        var territories = _data.GetExcelSheet<TerritoryType>();
        var residents = _data.GetExcelSheet<ENpcResident>();
        var rows = new List<Row>();
        var unplaced = new Dictionary<(string Npc, string Cost), int>(); // -> index in rows
        foreach (var seller in VendorMenu.Order(sellers, _territory(), _playerPosition()))
        {
            var name = residents.GetRowOrDefault(seller.NpcDataId)?.Singular.ExtractText();
            name = string.IsNullOrEmpty(name) ? "a vendor" : name;
            var cost = costs[(seller.NpcDataId, seller.ShopId)];
            if (seller.Territory != 0)
            {
                rows.Add(new Row(seller, name, Place(seller, territories), cost));
                continue;
            }

            // Nobody can be shown the way to these: one line a name is enough.
            if (unplaced.TryGetValue((name, cost), out var index))
            {
                var count = rows[index].Count + 1;
                rows[index] = rows[index] with { Count = count, Place = $"{count} of them — the game data does not say where (housing vendors, mostly)" };
            }
            else
            {
                unplaced[(name, cost)] = rows.Count;
                rows.Add(new Row(seller, name, "the game data does not say where", cost));
            }
        }

        return rows;
    }

    private static string Place(PlacedSeller seller, Lumina.Excel.ExcelSheet<TerritoryType> territories)
    {
        if (territories.GetRowOrDefault(seller.Territory) is not { } territory)
            return $"zone {seller.Territory}";

        var zone = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? $"zone {seller.Territory}";
        if (territory.Map.ValueNullable is not { } map)
            return zone;

        var at = MapUtil.WorldToMap(new Vector2(seller.Position.X, seller.Position.Z), map);
        return FormattableString.Invariant($"{zone} ({at.X:F1}, {at.Y:F1})");
    }
}
