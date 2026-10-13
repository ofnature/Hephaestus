using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Services.Game;

/// <summary>
/// Reports every crafting action the game ACCEPTS — ours or the player's — so the session can keep
/// the parts of the craft state the game does not display (the touch combo, spent once-per-craft
/// actions) and feed the evaluator what really happened in Assisted mode.
///
/// A hook on ActionManager.UseAction via the FFXIVClientStructs address (the same way Daedalus hooks
/// ActionEffect), so a game patch is absorbed by a ClientStructs update rather than a stale
/// signature of ours. It is ENABLED ONLY WHILE A SYNTHESIS IS OPEN: UseAction is also the combat
/// hot path, and this plugin has no business being in it.
/// </summary>
public sealed unsafe class CraftActionHook : IDisposable
{
    private readonly GameActionMap _actions;
    private readonly IPluginLog _log;
    private readonly Hook<ActionManager.Delegates.UseAction>? _hook;

    public CraftActionHook(IGameInteropProvider interop, GameActionMap actions, IPluginLog log)
    {
        _actions = actions;
        _log = log;

        try
        {
            _hook = interop.HookFromAddress<ActionManager.Delegates.UseAction>(
                (nint)ActionManager.MemberFunctionPointers.UseAction, Detour);
        }
        catch (Exception ex)
        {
            // Fail open: Auto mode still works (it knows what it pressed); Assisted loses combo tracking.
            _log.Error(ex, "Hephaestus: could not hook UseAction — actions pressed by hand will not be tracked");
        }
    }

    /// <summary>Raised on the framework thread for each accepted crafting action.</summary>
    public event Action<CraftAction>? ActionUsed;

    public bool Available => _hook != null;

    public void SetEnabled(bool enabled)
    {
        if (_hook == null || _hook.IsEnabled == enabled)
            return;

        if (enabled)
            _hook.Enable();
        else
            _hook.Disable();
    }

    public void Dispose() => _hook?.Dispose();

    private bool Detour(
        ActionManager* manager, ActionType type, uint actionId, ulong targetId, uint extraParam,
        ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        var accepted = _hook!.Original(manager, type, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

        try
        {
            if (accepted && (type is ActionType.CraftAction or ActionType.Action)
                         && _actions.TryGetAction(actionId, out var action))
                ActionUsed?.Invoke(action);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hephaestus: UseAction detour failed"); // never let ours take the game's call down
        }

        return accepted;
    }
}
