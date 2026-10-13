using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ENpcBase = Lumina.Excel.Sheets.ENpcBase;
using ENpcResident = Lumina.Excel.Sheets.ENpcResident;
using GilShopItem = Lumina.Excel.Sheets.GilShopItem;
using Item = Lumina.Excel.Sheets.Item;

namespace Hephaestus.Services.Game;

/// <summary>An NPC that sells something for gil, and the shop of theirs that stocks it.</summary>
public readonly record struct Vendor(uint NpcDataId, uint ShopId, string Name);

/// <summary>
/// Which items NPC vendors sell for gil, at what price, and who sells them — from the sheets, the
/// way Odysseus's IngredientSource does it (field-proven there): <c>GilShopItem</c> says what a shop
/// stocks, <c>ENpcBase.ENpcData</c> says which NPCs run which shops (an event handler id whose high
/// half is 4 is a gil shop). Only shops some NPC runs DIRECTLY are counted: a shop we cannot name a
/// seller for is a shop we cannot buy from, so its price is of no use to the planner.
///
/// Not here: where a vendor stands. The sheets above do not say; that comes with the travel step.
/// Indexed once, on first use. Fail-open: if the sheets cannot be read, nothing is for sale.
/// </summary>
public sealed class VendorCatalog
{
    /// <summary>ENpcData handler kind (id >> 16) of a gil shop.</summary>
    private const uint GilShopHandler = 4;

    /// <summary>
    /// Gil shops an NPC runs that ENpcData does not list. GBR's Vulcan carries this from AllaganLib's
    /// NpcShopCache (Apache-2.0); one entry, which says how rare the gap is for GIL shops.
    /// </summary>
    private static readonly (uint NpcId, uint ShopId)[] ExtraGilShops = { (1025763u, 262919u) };

    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private Dictionary<uint, List<uint>>? _shopsByItem;
    private Dictionary<uint, List<uint>>? _npcsByShop;

    public VendorCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    /// <summary>What one costs at a vendor, NQ; 0 when no vendor we can name sells it.</summary>
    public uint PriceOf(uint itemId)
    {
        EnsureIndexed();
        return _shopsByItem!.ContainsKey(itemId)
            ? _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.PriceMid ?? 0
            : 0;
    }

    /// <summary>
    /// Everyone who sells an item, in sheet order — all of them, not the first: seven NPCs sell Copper
    /// Ore, and the one standing next to you is rarely the first in the sheet (Odysseus learned this).
    /// </summary>
    public IReadOnlyList<Vendor> VendorsFor(uint itemId)
    {
        EnsureIndexed();
        var vendors = new List<Vendor>();
        if (!_shopsByItem!.TryGetValue(itemId, out var shops))
            return vendors;

        var residents = _data.GetExcelSheet<ENpcResident>();
        foreach (var shop in shops)
        {
            foreach (var npc in _npcsByShop![shop])
                vendors.Add(new Vendor(npc, shop, residents.GetRowOrDefault(npc)?.Singular.ExtractText() ?? string.Empty));
        }

        return vendors;
    }

    private void EnsureIndexed()
    {
        if (_shopsByItem != null)
            return;

        _shopsByItem = new Dictionary<uint, List<uint>>();
        _npcsByShop = new Dictionary<uint, List<uint>>();
        try
        {
            foreach (var npc in _data.GetExcelSheet<ENpcBase>())
            {
                foreach (var handler in npc.ENpcData)
                {
                    if (handler.RowId >> 16 != GilShopHandler)
                        continue;

                    if (!_npcsByShop.TryGetValue(handler.RowId, out var npcs))
                        _npcsByShop[handler.RowId] = npcs = new List<uint>(1);
                    npcs.Add(npc.RowId);
                }
            }

            foreach (var (npcId, shopId) in ExtraGilShops)
            {
                if (!_npcsByShop.TryGetValue(shopId, out var npcs))
                    _npcsByShop[shopId] = npcs = new List<uint>(1);
                if (!npcs.Contains(npcId))
                    npcs.Add(npcId);
            }

            foreach (var shop in _data.GetSubrowExcelSheet<GilShopItem>())
            {
                if (!_npcsByShop.ContainsKey(shop.RowId))
                    continue;

                foreach (var entry in shop)
                {
                    var itemId = entry.Item.RowId;
                    if (itemId == 0)
                        continue;

                    if (!_shopsByItem.TryGetValue(itemId, out var shops))
                        _shopsByItem[itemId] = shops = new List<uint>(1);
                    if (!shops.Contains(shop.RowId))
                        shops.Add(shop.RowId);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: could not index vendor shops — treating nothing as sold");
            _shopsByItem.Clear();
            _npcsByShop.Clear();
        }
    }
}
