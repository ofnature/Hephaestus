using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Services.Game;

/// <summary>
/// Presses a crafting action: ActionManager.UseAction directly, the same way Daedalus fires combat
/// actions (no key or hotbar simulation). The game remains the authority — a refusal is reported,
/// never forced; the session counts refusals and rules the action out.
/// </summary>
public sealed unsafe class CraftActionExecutor
{
    private readonly IObjectTable _objects;
    private readonly GameActionMap _actions;

    public CraftActionExecutor(IObjectTable objects, GameActionMap actions)
    {
        _objects = objects;
        _actions = actions;
    }

    /// <returns>True when the game accepted the action.</returns>
    public bool Use(CraftAction action)
    {
        var player = _objects.LocalPlayer;
        var manager = ActionManager.Instance();
        if (player == null || manager == null)
            return false;

        var id = _actions.IdFor(player.ClassJob.RowId, action);
        if (id == 0)
            return false;

        var type = id >= CarpenterActionIds.FirstCraftActionId ? ActionType.CraftAction : ActionType.Action;
        if (manager->GetActionStatus(type, id) != 0)
            return false;

        var used = manager->UseAction(type, id);
        if (used)
            ResetIdleTimers();
        return used;
    }

    /// <summary>
    /// Actions fired through UseAction are not keyboard input, so an unattended run would be
    /// flagged AFK and eventually kicked. Artisan resets the same three timers on every action.
    /// </summary>
    private static void ResetIdleTimers()
    {
        var ui = UIModule.Instance();
        if (ui == null)
            return;

        var timers = ui->GetInputTimerModule();
        if (timers == null)
            return;

        timers->AfkTimer = 0;
        timers->ContentInputTimer = 0;
        timers->InputTimer = 0;
    }
}
