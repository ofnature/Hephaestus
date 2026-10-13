using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Item = Lumina.Excel.Sheets.Item;
using ItemFood = Lumina.Excel.Sheets.ItemFood;

namespace Hephaestus.Services.Game;

/// <summary>A crafter food or medicine stack in the bags.</summary>
public readonly record struct Consumable(uint ItemId, bool Hq, string Name, int Count)
{
    public string Label => Hq ? $"{Name} (HQ) ×{Count}" : $"{Name} ×{Count}";
}

/// <summary>
/// Lists the crafter food and medicine in the bags, for the crafting-log window's pickers.
/// A consumable counts as "crafter" when its ItemFood row raises CP, Craftsmanship or Control —
/// the same test Artisan's ConsumableChecker uses.
/// </summary>
public sealed unsafe class ConsumableReader
{
    private const uint MedicineCategory = 44; // ItemUICategory
    private const uint MealCategory = 46;

    private static readonly InventoryType[] Bags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    };

    private static readonly TimeSpan CacheLife = TimeSpan.FromSeconds(1);

    private readonly IDataManager _data;
    private DateTime _readAt;

    public ConsumableReader(IDataManager data)
    {
        _data = data;
    }

    public IReadOnlyList<Consumable> Food { get; private set; } = Array.Empty<Consumable>();

    public IReadOnlyList<Consumable> Medicine { get; private set; } = Array.Empty<Consumable>();

    /// <summary>Re-scan the bags, at most once a second — this is called from a window's Draw.</summary>
    public void Refresh(DateTime now)
    {
        if (now - _readAt < CacheLife)
            return;
        _readAt = now;

        var counts = new Dictionary<(uint ItemId, bool Hq), int>();
        var inventory = InventoryManager.Instance();
        if (inventory != null)
        {
            foreach (var bag in Bags)
            {
                var container = inventory->GetInventoryContainer(bag);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0)
                        continue;

                    var key = (slot->ItemId, slot->IsHighQuality());
                    counts[key] = counts.GetValueOrDefault(key) + slot->Quantity;
                }
            }
        }

        var food = new List<Consumable>();
        var medicine = new List<Consumable>();
        foreach (var ((itemId, hq), count) in counts)
        {
            if (_data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is not { } item || !IsCrafterConsumable(item, hq))
                continue;

            var entry = new Consumable(itemId, hq, item.Name.ExtractText(), count);
            if (item.ItemUICategory.RowId == MealCategory)
                food.Add(entry);
            else if (item.ItemUICategory.RowId == MedicineCategory)
                medicine.Add(entry);
        }

        Food = food.OrderBy(c => c.Name).ThenBy(c => c.Hq).ToList();
        Medicine = medicine.OrderBy(c => c.Name).ThenBy(c => c.Hq).ToList();
    }

    private bool IsCrafterConsumable(Item item, bool hq)
    {
        if (item.ItemAction.ValueNullable is not { } action)
            return false;

        // ItemAction data: [0] = status, [1] = ItemFood row, [2] = duration.
        var foodRow = hq ? action.DataHQ[1] : action.Data[1];
        return _data.GetExcelSheet<ItemFood>().GetRowOrDefault(foodRow) is { } itemFood
               && itemFood.Params.Any(p => p.BaseParam.RowId is 11 or 70 or 71); // CP / Craftsmanship / Control
    }
}
