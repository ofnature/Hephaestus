using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;
using GameAction = Lumina.Excel.Sheets.Action;
using GameCraftAction = Lumina.Excel.Sheets.CraftAction;

namespace Hephaestus.Services.Game;

/// <summary>
/// Our <see cref="CraftAction"/>s as the game knows them: a different action id per crafting job,
/// plus the icon. Found by matching the Carpenter action's NAME in the sheets — the client's own
/// language on both sides, so it is language-independent. Built once, in a single pass over each
/// sheet that checks the (cheap) job column before it touches any text.
/// </summary>
public sealed class GameActionMap
{
    public const uint FirstCraftingJob = 8; // Carpenter
    public const uint LastCraftingJob = 15; // Culinarian

    private readonly IDataManager _data;
    private readonly Dictionary<(uint Job, CraftAction Action), uint> _ids = new();
    private readonly Dictionary<uint, CraftAction> _actions = new();
    private readonly Dictionary<CraftAction, uint> _icons = new();
    private bool _built;

    public GameActionMap(IDataManager data)
    {
        _data = data;
    }

    public static bool IsCraftingJob(uint classJobId) => classJobId is >= FirstCraftingJob and <= LastCraftingJob;

    /// <summary>The job's own id for an action; 0 when it cannot be resolved.</summary>
    public uint IdFor(uint classJobId, CraftAction action)
    {
        Build();
        return _ids.GetValueOrDefault((classJobId, action));
    }

    /// <summary>Which of our actions a game action id is, for any crafting job.</summary>
    public bool TryGetAction(uint actionId, out CraftAction action)
    {
        Build();
        return _actions.TryGetValue(actionId, out action);
    }

    public uint IconFor(CraftAction action)
    {
        Build();
        return _icons.GetValueOrDefault(action);
    }

    private void Build()
    {
        if (_built)
            return;
        _built = true;

        var craftActions = _data.GetExcelSheet<GameCraftAction>();
        var actions = _data.GetExcelSheet<GameAction>();

        // The Carpenter rows give us each action's name (in the client's language) and icon.
        var craftNames = new Dictionary<string, CraftAction>();
        var actionNames = new Dictionary<string, CraftAction>();
        foreach (var (action, carpenterId) in CarpenterActionIds.All)
        {
            if (carpenterId >= CarpenterActionIds.FirstCraftActionId)
            {
                if (craftActions.GetRowOrDefault(carpenterId) is not { } row)
                    continue;
                craftNames[row.Name.ExtractText()] = action;
                _icons[action] = row.Icon;
            }
            else
            {
                if (actions.GetRowOrDefault(carpenterId) is not { } row)
                    continue;
                actionNames[row.Name.ExtractText()] = action;
                _icons[action] = row.Icon;
            }
        }

        // ClassJobCategory rows 9..16 are the single-job categories CRP..CUL, i.e. job id + 1.
        foreach (var row in craftActions)
        {
            var job = row.ClassJobCategory.RowId - 1;
            if (IsCraftingJob(job) && craftNames.TryGetValue(row.Name.ExtractText(), out var action))
                Add(job, action, row.RowId);
        }

        foreach (var row in actions)
        {
            var job = row.ClassJob.RowId;
            if (IsCraftingJob(job) && actionNames.TryGetValue(row.Name.ExtractText(), out var action))
                Add(job, action, row.RowId);
        }

        // Cosmic Exploration's duty actions belong to no job (ClassJob -1): the one row serves all eight.
        foreach (var duty in new[] { CraftAction.StellarSteadyHand, CraftAction.MaterialMiracle })
        {
            for (uint job = FirstCraftingJob; job <= LastCraftingJob; job++)
                Add(job, duty, CarpenterActionIds.All[duty]);
        }
    }

    private void Add(uint job, CraftAction action, uint id)
    {
        _ids.TryAdd((job, action), id);
        _actions.TryAdd(id, action);
    }
}
