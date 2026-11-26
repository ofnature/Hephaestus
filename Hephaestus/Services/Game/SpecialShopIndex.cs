using System;
using System.Collections.Generic;
using Hephaestus.Features.Materials;
using Lumina.Excel;
using Addon = Lumina.Excel.Sheets.Addon;
using CustomTalk = Lumina.Excel.Sheets.CustomTalk;
using CustomTalkNestHandlers = Lumina.Excel.Sheets.CustomTalkNestHandlers;
using ENpcBase = Lumina.Excel.Sheets.ENpcBase;
using FateShop = Lumina.Excel.Sheets.FateShop;
using FccShop = Lumina.Excel.Sheets.FccShop;
using GCScripShopCategory = Lumina.Excel.Sheets.GCScripShopCategory;
using GCScripShopItem = Lumina.Excel.Sheets.GCScripShopItem;
using GCShop = Lumina.Excel.Sheets.GCShop;
using GilShopItem = Lumina.Excel.Sheets.GilShopItem;
using InclusionShop = Lumina.Excel.Sheets.InclusionShop;
using Item = Lumina.Excel.Sheets.Item;
using PreHandler = Lumina.Excel.Sheets.PreHandler;
using SpecialShop = Lumina.Excel.Sheets.SpecialShop;
using TomestonesItem = Lumina.Excel.Sheets.TomestonesItem;
using TopicSelect = Lumina.Excel.Sheets.TopicSelect;

namespace Hephaestus.Services.Game;

/// <summary>Someone who sells an item for something other than gil across a counter - or for gil, behind a menu.</summary>
/// <param name="ShopId">The event handler of the shop the item is in.</param>
/// <param name="Cost">In words: "600 × Storm Seal".</param>
/// <param name="GrandCompany">1-3 when only members of that Grand Company are served; else 0.</param>
public readonly record struct SpecialSeller(uint NpcDataId, uint ShopId, string Cost, byte GrandCompany = 0);

/// <summary>
/// WHO SELLS WHAT THAT <see cref="VendorCatalog"/> DOES NOT KNOW (user, 2026-11-26: "can we do special merchants
/// also?"): Grand Company seals, special shops (scrips, tomestones, an item for an item), FC credits, the shops
/// of FATE vendors, and every shop - gil ones too - that sits behind a vendor's menu. FOR SHOWING ONLY (the Vendor
/// Location window): the material planner and the vendor trips buy from <see cref="VendorCatalog"/> alone, for gil,
/// from shops an NPC runs directly. Never feed this to them.
/// <para>
/// The walk is the one Item Vendor Location makes (its source read, none of it copied): an NPC's
/// <c>ENpcData</c> handlers by kind, through <c>TopicSelect</c>, <c>PreHandler</c>, <c>CustomTalk</c> and
/// <c>InclusionShop</c> down to the shops. Left out: its hand-kept lists (relic and quest-reward vendors, a few
/// NPCs the sheets do not tie to their shop), collectable appraisers and achievement rewards.
/// </para>
/// Lumina only - no Dalamud types - so it can be run against the game data outside the game
/// (<c>.build/vendor-probe --special</c>).
/// </summary>
public static class SpecialShopIndex
{
    private const uint GilShop = 0x04, CustomTalkKind = 0x0B, GcShop = 0x16, SpecialShopKind = 0x1B, FcShop = 0x2A,
        TopicSelectKind = 0x32, PreHandlerKind = 0x36, InclusionShopKind = 0x3A;

    /// <summary>Items 20-22 are the Storm, Serpent and Flame Seal, in Grand Company order.</summary>
    private const uint FirstSealItem = 19;

    /// <summary>Addon row that names company credits.</summary>
    private const uint CompanyCreditText = 102233;

    private static uint Kind(uint handler) => handler >> 16;

    /// <summary>Item id -> everyone in the sheets who sells it this way. Reads sheets only; safe off the framework thread.</summary>
    public static Dictionary<uint, List<SpecialSeller>> Build(ExcelModule excel)
    {
        var walk = new Walk(excel);
        foreach (var npc in excel.GetSheet<ENpcBase>())
            walk.Npc(npc);
        return walk.Found;
    }

    private sealed class Walk
    {
        public readonly Dictionary<uint, List<SpecialSeller>> Found = new();

        private readonly ExcelSheet<Item> _items;
        private readonly ExcelSheet<SpecialShop> _specialShops;
        private readonly ExcelSheet<GCShop> _gcShops;
        private readonly ExcelSheet<GCScripShopCategory> _gcCategories;
        private readonly SubrowExcelSheet<GCScripShopItem> _gcItems;
        private readonly SubrowExcelSheet<GilShopItem> _gilItems;
        private readonly ExcelSheet<InclusionShop> _inclusionShops;
        private readonly ExcelSheet<FccShop> _fccShops;
        private readonly ExcelSheet<FateShop> _fateShops;
        private readonly ExcelSheet<TopicSelect> _topicSelects;
        private readonly ExcelSheet<PreHandler> _preHandlers;
        private readonly ExcelSheet<CustomTalk> _customTalks;
        private readonly SubrowExcelSheet<CustomTalkNestHandlers> _nestHandlers;
        private readonly Dictionary<uint, uint> _tomestones = new();
        private readonly string _companyCredits;
        private readonly HashSet<(uint Item, uint Npc, uint Shop, string Cost)> _seen = new();

        public Walk(ExcelModule excel)
        {
            _items = excel.GetSheet<Item>();
            _specialShops = excel.GetSheet<SpecialShop>();
            _gcShops = excel.GetSheet<GCShop>();
            _gcCategories = excel.GetSheet<GCScripShopCategory>();
            _gcItems = excel.GetSubrowSheet<GCScripShopItem>();
            _gilItems = excel.GetSubrowSheet<GilShopItem>();
            _inclusionShops = excel.GetSheet<InclusionShop>();
            _fccShops = excel.GetSheet<FccShop>();
            _fateShops = excel.GetSheet<FateShop>();
            _topicSelects = excel.GetSheet<TopicSelect>();
            _preHandlers = excel.GetSheet<PreHandler>();
            _customTalks = excel.GetSheet<CustomTalk>();
            _nestHandlers = excel.GetSubrowSheet<CustomTalkNestHandlers>();
            foreach (var row in excel.GetSheet<TomestonesItem>())
            {
                if (row.Tomestones.RowId != 0)
                    _tomestones.TryAdd(row.Tomestones.RowId, row.Item.RowId);
            }

            var credits = excel.GetSheet<Addon>().GetRowOrDefault(CompanyCreditText)?.Text.ExtractText();
            _companyCredits = string.IsNullOrEmpty(credits) ? "company credits" : credits;
        }

        public void Npc(ENpcBase npc)
        {
            // A FATE vendor's shops hang off the NPC's own id.
            if (_fateShops.GetRowOrDefault(npc.RowId) is { } fate)
            {
                foreach (var shop in fate.SpecialShop)
                    Special(npc.RowId, shop.RowId);
            }

            foreach (var handlerRef in npc.ENpcData)
            {
                var handler = handlerRef.RowId;
                switch (Kind(handler))
                {
                    case SpecialShopKind: Special(npc.RowId, handler); break;
                    case GcShop: GrandCompany(npc.RowId, handler); break;
                    case FcShop: FreeCompany(npc.RowId, handler); break;
                    case InclusionShopKind: Inclusion(npc.RowId, handler); break;
                    case PreHandlerKind: Behind(npc.RowId, handler); break;
                    case TopicSelectKind:
                        if (_topicSelects.GetRowOrDefault(handler) is { } topic)
                        {
                            foreach (var shop in topic.Shop)
                                Behind(npc.RowId, shop.RowId);
                        }

                        break;
                    case CustomTalkKind:
                        if (_customTalks.GetRowOrDefault(handler) is { } talk)
                        {
                            if (talk.SpecialLinks.RowId != 0 && _nestHandlers.TryGetRow(talk.SpecialLinks.RowId, out var nest))
                            {
                                foreach (var link in nest)
                                    Behind(npc.RowId, link.NestHandler.RowId, preHandlers: false);
                            }

                            foreach (var script in talk.Script)
                                Behind(npc.RowId, script.ScriptArg, preHandlers: false);
                        }

                        break;
                }
            }
        }

        /// <summary>A shop reached through a menu: whatever kind it is. Gil shops count here - a counter one does not.</summary>
        private void Behind(uint npc, uint handler, bool preHandlers = true)
        {
            switch (Kind(handler))
            {
                case GilShop: Gil(npc, handler); break;
                case SpecialShopKind: Special(npc, handler); break;
                case FcShop: FreeCompany(npc, handler); break;
                case InclusionShopKind: Inclusion(npc, handler); break;
                case PreHandlerKind when preHandlers:
                    if (_preHandlers.GetRowOrDefault(handler) is { } pre)
                        Behind(npc, pre.Target.RowId, preHandlers: false);
                    break;
            }
        }

        private void Gil(uint npc, uint shopId)
        {
            if (!_gilItems.TryGetRow(shopId, out var stock))
                return;

            foreach (var entry in stock)
            {
                if (entry.Item.RowId != 0 && _items.GetRowOrDefault(entry.Item.RowId) is { } item)
                    Add(item.RowId, npc, shopId, SpecialCost.Text(new[] { (item.PriceMid, SpecialCost.Gil, "gil") }));
            }
        }

        private void Special(uint npc, uint shopId)
        {
            if (shopId == 0 || _specialShops.GetRowOrDefault(shopId) is not { } shop)
                return;

            foreach (var entry in shop.Item)
            {
                var costs = new List<(uint, uint, string)>();
                foreach (var cost in entry.ItemCosts)
                {
                    if (cost.ItemCost.RowId == 0 || cost.CurrencyCost == 0)
                        continue;

                    var itemId = SpecialCost.CurrencyItem(
                        cost.ItemCost.RowId, shop.UseCurrencyType, shopId, slot => _tomestones.GetValueOrDefault(slot));
                    costs.Add((cost.CurrencyCost, itemId, ItemName(itemId)));
                }

                var text = SpecialCost.Text(costs);
                if (text.Length == 0)
                    continue;

                foreach (var received in entry.ReceiveItems)
                {
                    if (received.Item.RowId != 0)
                        Add(received.Item.RowId, npc, shopId, text);
                }
            }
        }

        private void GrandCompany(uint npc, uint shopId)
        {
            if (_gcShops.GetRowOrDefault(shopId) is not { } shop || shop.GrandCompany.RowId is 0 or > 3)
                return;

            var company = (byte)shop.GrandCompany.RowId;
            var seal = ItemName(FirstSealItem + company);
            foreach (var category in _gcCategories)
            {
                if (category.GrandCompany.RowId != company || !_gcItems.TryGetRow(category.RowId, out var stock))
                    continue;

                foreach (var entry in stock)
                {
                    if (entry.Item.RowId != 0 && entry.SortKey != 0 && entry.CostGCSeals != 0)
                        Add(entry.Item.RowId, npc, shopId, SpecialCost.Text(new[] { (entry.CostGCSeals, FirstSealItem + company, seal) }), company);
                }
            }
        }

        private void FreeCompany(uint npc, uint shopId)
        {
            if (_fccShops.GetRowOrDefault(shopId) is not { } shop)
                return;

            foreach (var entry in shop.ItemData)
            {
                if (entry.Item.RowId != 0 && entry.Cost != 0)
                    Add(entry.Item.RowId, npc, shopId, SpecialCost.Text(new[] { (entry.Cost, 0u, _companyCredits) }));
            }
        }

        private void Inclusion(uint npc, uint shopId)
        {
            if (_inclusionShops.GetRowOrDefault(shopId) is not { } shop)
                return;

            foreach (var categoryRef in shop.Category)
            {
                if (categoryRef.RowId == 0 || categoryRef.ValueNullable is not { } category)
                    continue;

                try
                {
                    foreach (var series in category.InclusionShopSeries.Value)
                        Special(npc, series.SpecialShop.RowId);
                }
                catch (Exception)
                {
                    // A category with no series behind it: nothing to list.
                }
            }
        }

        private void Add(uint itemId, uint npc, uint shopId, string cost, byte company = 0)
        {
            if (cost.Length == 0 || !_seen.Add((itemId, npc, shopId, cost)))
                return;

            if (!Found.TryGetValue(itemId, out var sellers))
                Found[itemId] = sellers = new List<SpecialSeller>(1);
            sellers.Add(new SpecialSeller(npc, shopId, cost, company));
        }

        private string ItemName(uint itemId)
            => itemId == 0 ? string.Empty : _items.GetRowOrDefault(itemId)?.Name.ExtractText() ?? string.Empty;
    }
}
