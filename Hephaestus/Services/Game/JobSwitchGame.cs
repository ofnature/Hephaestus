using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using ClassJob = Lumina.Excel.Sheets.ClassJob;

namespace Hephaestus.Services.Game;

/// <summary>
/// Changing crafting job, the way Charon's JobSwitcher does it: find the saved gearset whose job
/// matches and ask RaptureGearsetModule to equip it — never the Gear Set List window, never an
/// improvised swap of individual pieces. Whether it WORKED is not this class's call: the queue
/// watches the player's job change (or not) and decides.
/// </summary>
public sealed unsafe class JobSwitchGame
{
    private readonly IObjectTable _objects;
    private readonly ICondition _condition;
    private readonly IDataManager _data;
    private readonly IPluginLog _log;

    public JobSwitchGame(IObjectTable objects, ICondition condition, IDataManager data, IPluginLog log)
    {
        _objects = objects;
        _condition = condition;
        _data = data;
        _log = log;
    }

    /// <summary>The player's ClassJob row; 0 when not logged in.</summary>
    public uint CurrentJob => _objects.LocalPlayer?.ClassJob.RowId ?? 0;

    /// <summary>The game would accept a gearset change right now.</summary>
    public bool CanSwitch
        => !_condition[ConditionFlag.Crafting]
           && !_condition[ConditionFlag.PreparingToCraft]
           && !_condition[ConditionFlag.ExecutingCraftingAction]
           && !_condition[ConditionFlag.InCombat]
           && !_condition[ConditionFlag.Occupied]
           && !_condition[ConditionFlag.OccupiedInEvent]
           && !_condition[ConditionFlag.OccupiedInQuestEvent]
           && !_condition[ConditionFlag.OccupiedInCutSceneEvent]
           && !_condition[ConditionFlag.BetweenAreas];

    public bool HasGearsetFor(uint classJobId) => FindGearsetFor(classJobId) >= 0;

    /// <summary>The character's level on a job, whichever job is equipped; 0 when it cannot be read or the job is not unlocked.</summary>
    public int LevelOf(uint classJobId)
    {
        try
        {
            var state = PlayerState.Instance();
            if (state == null || _data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId) is not { } job || job.ExpArrayIndex < 0)
                return 0;

            return state->ClassJobLevels[job.ExpArrayIndex];
        }
        catch
        {
            return 0;
        }
    }

    /// <returns>True when the game accepted the request. The job changes a moment later — or does not; watch <see cref="CurrentJob"/>.</returns>
    public bool Equip(uint classJobId)
    {
        try
        {
            var index = FindGearsetFor(classJobId);
            if (index < 0)
                return false;

            var result = RaptureGearsetModule.Instance()->EquipGearset(index);
            if (result < 0)
                _log.Warning("Hephaestus: EquipGearset({0}) for job {1} returned {2}", index, classJobId, result);
            return result >= 0;
        }
        catch (System.Exception ex)
        {
            _log.Warning(ex, "Hephaestus: gearset change threw");
            return false;
        }
    }

    private static int FindGearsetFor(uint classJobId)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null)
            return -1;

        for (var i = 0; i < module->NumGearsets; i++)
        {
            if (!module->IsValidGearset(i))
                continue;

            var gearset = module->GetGearset(i);
            if (gearset != null && gearset->ClassJob == classJobId)
                return i;
        }

        return -1;
    }
}
