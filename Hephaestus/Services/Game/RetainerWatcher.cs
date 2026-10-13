using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Hephaestus.Features.Materials;

namespace Hephaestus.Services.Game;

/// <summary>
/// Learns the retainer index passively. It opens nothing: whenever a retainer's bags are open —
/// the player at a bell, or a trip of ours — <c>AgentRetainer</c> is active and the retainer pages
/// are loaded, so they are read (every couple of seconds while open, since withdrawals change them)
/// and remembered for that retainer. The roster (which retainers exist) is read whenever the game
/// has it, so "known but never opened" can be told from "does not exist". Saved when the bags close.
/// </summary>
public sealed unsafe class RetainerWatcher
{
    private static readonly InventoryType[] Pages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    };

    private static readonly TimeSpan Resample = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RosterEvery = TimeSpan.FromSeconds(30);

    private readonly CharacterRetainers _retainers;
    private readonly IPluginLog _log;
    private ulong _openRetainer;
    private DateTime _nextSample;
    private DateTime _nextRoster;
    private bool _dirty;

    public RetainerWatcher(CharacterRetainers retainers, IPluginLog log)
    {
        _retainers = retainers;
        _log = log;
    }

    public string Status { get; private set; } = "Idle — opens nothing; it learns from retainers you open.";

    /// <summary>The retainer whose bags are open right now; 0 = none.</summary>
    public ulong OpenRetainer => _openRetainer;

    public void Tick(DateTime now)
    {
        if (_retainers.ContentId == 0)
            return;

        try
        {
            var manager = RetainerManager.Instance();
            if (manager == null || !manager->IsReady)
                return;

            if (now >= _nextRoster)
            {
                _nextRoster = now + RosterEvery;
                ReadRoster(manager);
            }

            var agent = AgentRetainer.Instance();
            var active = agent != null && agent->IsAgentActive() ? manager->GetActiveRetainer() : null;
            if (active == null || active->RetainerId == 0)
            {
                if (_openRetainer != 0)
                    Closed();
                return;
            }

            if (active->RetainerId != _openRetainer)
            {
                _openRetainer = active->RetainerId;
                _nextSample = default;
            }

            if (now < _nextSample)
                return;

            _nextSample = now + Resample;
            if (ReadBags() is not { } items)
                return; // a page not loaded yet: next time

            var name = active->NameString;
            _retainers.Index.Learn(_openRetainer, name, items, now);
            _dirty = true;
            Status = $"Indexed {name}: {items.Count} item stack(s).";
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Hephaestus: reading retainers failed this tick");
        }
    }

    /// <summary>Another character logged in: whatever was open is not theirs.</summary>
    public void Forget()
    {
        _openRetainer = 0;
        _dirty = false;
        _nextRoster = default;
    }

    private void Closed()
    {
        _openRetainer = 0;
        if (_dirty)
        {
            _retainers.Save();
            _dirty = false;
        }

        var index = _retainers.Index;
        Status = $"{index.Retainers.Count} retainer(s) known, {index.NeverIndexed().Count} never opened.";
    }

    private void ReadRoster(RetainerManager* manager)
    {
        var ids = new List<ulong>();
        foreach (var retainer in manager->Retainers)
        {
            if (retainer.RetainerId == 0)
                continue;

            ids.Add(retainer.RetainerId);
            _retainers.Index.Know(retainer.RetainerId, retainer.NameString);
        }

        if (ids.Count > 0)
            _retainers.Index.Prune(ids);
    }

    /// <summary>Every page of the open retainer, NQ and HQ apart; null while a page is still loading.</summary>
    private static Dictionary<uint, ItemCount>? ReadBags()
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
            return null;

        var items = new Dictionary<uint, ItemCount>();
        foreach (var page in Pages)
        {
            var container = inventory->GetInventoryContainer(page);
            if (container == null || !container->IsLoaded)
                return null;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || slot->Quantity == 0)
                    continue;

                var hq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                var count = items.GetValueOrDefault(slot->ItemId);
                items[slot->ItemId] = hq
                    ? count with { Hq = count.Hq + (int)slot->Quantity }
                    : count with { Nq = count.Nq + (int)slot->Quantity };
            }
        }

        return items;
    }
}
