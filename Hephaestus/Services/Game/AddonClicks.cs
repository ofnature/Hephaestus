using System;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Hephaestus.Services.Game;

/// <summary>The few game dialogs a trip has to click through, shared by the vendor and retainer worlds.</summary>
public static unsafe class AddonClicks
{
    /// <summary>
    /// The subtitle box advances on a click, and this is that click: a fresh AtkEvent aimed at the
    /// addon and a MouseDown/Click/Up triple. Odysseus's port of ECommons' AddonMaster.Talk.Click.
    /// </summary>
    /// <returns>True when a Talk box was up and has been clicked.</returns>
    public static bool AdvanceTalk(IGameGui gameGui)
    {
        var addon = gameGui.GetAddonByName("Talk");
        if (addon.IsNull || !addon.IsVisible)
            return false;

        var unit = (AtkUnitBase*)addon.Address;
        var evt = new AtkEvent
        {
            Listener = (AtkEventListener*)unit,
            Target = &AtkStage.Instance()->AtkEventTarget,
            State = new() { StateFlags = (AtkEventStateFlags)132 },
        };
        var data = default(AtkEventData);
        unit->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
        unit->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
        unit->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
        return true;
    }

    /// <summary>A visible SelectString, or null.</summary>
    public static AddonSelectString* SelectString(IGameGui gameGui)
    {
        var addon = gameGui.GetAddonByName("SelectString");
        return addon.IsNull || !addon.IsVisible ? null : (AddonSelectString*)addon.Address;
    }

    /// <summary>The entries of a SelectString, as text.</summary>
    public static string[] Entries(AddonSelectString* menu)
    {
        var count = menu->PopupMenu.PopupMenu.EntryCount;
        var entries = new string[Math.Max(0, count)];
        for (var i = 0; i < entries.Length; i++)
        {
            var name = menu->PopupMenu.PopupMenu.EntryNames[i].Value;
            entries[i] = name == null ? string.Empty : MemoryHelper.ReadSeStringNullTerminated((nint)name).TextValue;
        }

        return entries;
    }

    /// <summary>
    /// Choose the entry whose text contains <paramref name="containing"/> (case-insensitive), else the
    /// fallback index (-1 = the last). The click is the addon's own callback with the index.
    /// </summary>
    public static bool ChooseEntry(AddonSelectString* menu, string containing, int fallbackIndex)
    {
        var entries = Entries(menu);
        if (entries.Length == 0)
            return false;

        var index = Array.FindIndex(entries, e => e.Contains(containing, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            index = fallbackIndex < 0 ? entries.Length - 1 : fallbackIndex;
        if (index >= entries.Length)
            return false;

        return menu->AtkUnitBase.FireCallbackInt(index);
    }
}
