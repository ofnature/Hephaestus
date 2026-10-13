using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Services.Game;

/// <summary>A hotbar slot on screen, in game-window pixels.</summary>
public readonly record struct HotbarSlotRect(Vector2 Position, Vector2 Size);

/// <summary>
/// Finds where an action sits on the VISIBLE hotbars. What a slot holds comes from
/// RaptureHotbarModule (typed: command kind + id, and the action it currently appears as); where
/// it is comes from the action-bar addon's own slot list. The addon's RaptureHotbarId says which
/// hotbar it is showing right now, so cycled and shared hotbars are followed correctly.
///
/// Two independent sources are cross-checked: a slot is only reported when the addon's slot agrees
/// about the action id. If a patch ever breaks the index alignment between the two, the highlight
/// disappears instead of pointing at the wrong button.
/// </summary>
public sealed unsafe class HotbarReader
{
    private static readonly string[] Bars =
    {
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
        "_ActionCross",
    };

    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objects;
    private readonly GameActionMap _actions;
    private readonly List<HotbarSlotRect> _found = new();

    /// <summary>
    /// Slots RaptureHotbarModule said hold the action on the last <see cref="Find"/>, before the addon
    /// cross-check. More here than in the result means the two sources disagree — worth reporting.
    /// </summary>
    public int LastCandidates { get; private set; }

    public HotbarReader(IGameGui gameGui, IObjectTable objects, GameActionMap actions)
    {
        _gameGui = gameGui;
        _objects = objects;
        _actions = actions;
    }

    /// <summary>Every visible slot holding <paramref name="action"/>. The list is reused between calls — read it, do not keep it.</summary>
    public IReadOnlyList<HotbarSlotRect> Find(CraftAction action)
    {
        _found.Clear();
        LastCandidates = 0;

        try
        {
            var player = _objects.LocalPlayer;
            var module = RaptureHotbarModule.Instance();
            if (player == null || module == null)
                return _found;

            var job = player.ClassJob.RowId;
            var actionId = _actions.IdFor(job, action);
            var fallbackId = HotbarMatcher.FallbackFor(action) is { } fallback ? _actions.IdFor(job, fallback) : 0u;
            if (actionId == 0)
                return _found;

            foreach (var name in Bars)
            {
                var bar = (AddonActionBarBase*)_gameGui.GetAddonByName(name).Address;
                if (bar == null || !bar->AtkUnitBase.IsVisible || bar->DisplayPetBar)
                    continue;
                if (bar->RaptureHotbarId >= module->Hotbars.Length)
                    continue;

                ref var hotbar = ref module->Hotbars[bar->RaptureHotbarId];
                var shown = bar->ActionBarSlotVector.AsSpan();
                var count = Math.Min(Math.Min(bar->SlotCount, shown.Length), hotbar.Slots.Length);

                for (var i = 0; i < count; i++)
                {
                    ref var slot = ref hotbar.Slots[i];
                    if (!HotbarMatcher.Matches(
                            KindOf(slot.CommandType), slot.CommandId,
                            KindOf(slot.ApparentSlotType), slot.ApparentActionId,
                            actionId, fallbackId))
                        continue;

                    LastCandidates++;

                    // Cross-check with what the addon itself says this button is.
                    var shownId = (uint)shown[i].ActionId;
                    if (shownId != actionId && shownId != fallbackId && shownId != slot.CommandId)
                        continue;

                    var icon = shown[i].Icon;
                    if (icon == null || !icon->AtkResNode.IsVisible())
                        continue;

                    var node = &icon->AtkResNode;
                    var scale = ScaleOf(node);
                    _found.Add(new HotbarSlotRect(
                        new Vector2(node->ScreenX, node->ScreenY),
                        new Vector2(node->Width * scale.X, node->Height * scale.Y)));
                }
            }
        }
        catch
        {
            _found.Clear(); // fail open: no highlight rather than a wrong one
        }

        return _found;
    }

    private static HotbarCommandKind KindOf(RaptureHotbarModule.HotbarSlotType type) => type switch
    {
        RaptureHotbarModule.HotbarSlotType.Action => HotbarCommandKind.Action,
        RaptureHotbarModule.HotbarSlotType.CraftAction => HotbarCommandKind.CraftAction,
        _ => HotbarCommandKind.Other,
    };

    /// <summary>A node's on-screen scale is its own times every ancestor's (HUD layout scaling lives on the root).</summary>
    private static Vector2 ScaleOf(AtkResNode* node)
    {
        var scale = Vector2.One;
        for (var current = node; current != null; current = current->ParentNode)
            scale *= new Vector2(current->ScaleX, current->ScaleY);
        return scale;
    }
}
