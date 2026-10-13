using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Workshop;
using Item = Lumina.Excel.Sheets.Item;
using CompanyCraftProcess = Lumina.Excel.Sheets.CompanyCraftProcess;
using CompanyCraftSequence = Lumina.Excel.Sheets.CompanyCraftSequence;
using CompanyCraftType = Lumina.Excel.Sheets.CompanyCraftType;

namespace Hephaestus.Services.Game;

/// <summary>
/// Reads a FC-workshop part's build from the CompanyCraft sheets. A part is a <see cref="CompanyCraftSequence"/>
/// whose result item is the part; its single CompanyCraftPart holds the phases as CompanyCraftProcess entries, and
/// each phase's materials are the process's parallel SupplyItem / SetQuantity / SetsRequired columns. A material's
/// total is <c>SetQuantity × SetsRequired</c> (that many sets, that much of the item per set).
/// </summary>
public sealed class CompanyCraftReader
{
    private readonly IDataManager _data;

    public CompanyCraftReader(IDataManager data) => _data = data;

    /// <summary>The part's build, or null when <paramref name="partItemId"/> is not a workshop part.</summary>
    public WorkshopBuild? Build(uint partItemId)
    {
        if (partItemId == 0)
            return null;

        foreach (var sequence in _data.GetExcelSheet<CompanyCraftSequence>())
        {
            if (sequence.ResultItem.RowId != partItemId)
                continue;

            var phases = new List<WorkshopPhase>();
            foreach (var partRef in sequence.CompanyCraftPart)
            {
                if (partRef.RowId == 0)
                    continue;

                var number = 0;
                foreach (var processRef in partRef.Value.CompanyCraftProcess)
                {
                    if (processRef.RowId == 0)
                        continue;

                    number++;
                    phases.Add(new WorkshopPhase(number, ReadMaterials(processRef.Value)));
                }
            }

            return new WorkshopBuild(partItemId, ItemName(partItemId), phases);
        }

        return null;
    }

    /// <summary>Every workshop part, grouped for a picker: its item, name and type label (e.g. "Airship Hull").</summary>
    public IReadOnlyList<WorkshopPartInfo> AllParts()
    {
        var list = new List<WorkshopPartInfo>();
        foreach (var sequence in _data.GetExcelSheet<CompanyCraftSequence>())
        {
            if (sequence.ResultItem.RowId == 0)
                continue;

            var typeName = sequence.CompanyCraftType.RowId == 0
                ? "Other"
                : _data.GetExcelSheet<CompanyCraftType>().GetRowOrDefault(sequence.CompanyCraftType.RowId)?.Name.ExtractText() ?? "Other";

            list.Add(new WorkshopPartInfo(sequence.ResultItem.RowId, ItemName(sequence.ResultItem.RowId), typeName));
        }

        return list.OrderBy(p => p.TypeName).ThenBy(p => p.Name).ToList();
    }

    private List<WorkshopMaterial> ReadMaterials(CompanyCraftProcess process)
    {
        var materials = new List<WorkshopMaterial>();
        var count = process.SupplyItem.Count();
        for (var i = 0; i < count; i++)
        {
            var supplyRef = process.SupplyItem[i];
            if (supplyRef.RowId == 0)
                continue;

            var itemId = supplyRef.Value.Item.RowId;
            if (itemId == 0)
                continue;

            var total = (uint)(process.SetQuantity[i] * process.SetsRequired[i]);
            materials.Add(new WorkshopMaterial(itemId, ItemName(itemId), total));
        }

        return materials;
    }

    private string ItemName(uint itemId)
        => _data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"item {itemId}";
}
