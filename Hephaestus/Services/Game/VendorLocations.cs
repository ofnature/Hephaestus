using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Level = Lumina.Excel.Sheets.Level;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace Hephaestus.Services.Game;

/// <summary>Where an NPC stands: the zone, and the spot in it.</summary>
public readonly record struct NpcLocation(uint TerritoryId, Vector3 Position);

/// <summary>
/// Where vendors stand, from game data — what the vendor trip needs in order to travel to a seller
/// that is NOT already spawned around the character. (A seller that IS spawned is found live, in
/// the object table, and needs none of this: that is Odysseus's pattern and stays the first choice.)
///
/// Two sources, measured against the real game data on 2026-09-20 (scratch probe, 525 recipe
/// ingredients sold in a gil shop an NPC runs directly):
///   - the <c>Level</c> sheet alone places a seller for 308 of them;
///   - adding every zone's <c>planevent.lgb</c> brings that to 509 (97%). All 1,134 files parse in
///     ~0.6 s and ~170 MB.
///
/// DELIBERATELY NOT READ: <c>planner.lgb</c> and <c>bg.lgb</c>. <c>planner.lgb</c> is where the
/// guild suppliers are (it would add a few more items, Iron Ore's only direct seller among them),
/// but Lumina's layout parser reads a runaway allocation out of some of those files: the probe
/// reported ~82 GB of managed memory after THREE files, and an unguarded run grew to 17.8 GB before
/// it was killed. Inside the game process that is a frozen or crashed client. A sample of ten city
/// zones parsed cleanly, so a small test does NOT show this — do not "just add planner.lgb" without
/// an out-of-process size guard. Items it would have placed degrade to the live same-zone check.
///
/// Built once, on a background task (0.6 s would hitch the game; it reads data files, no game
/// state). Until it finishes, and if it fails, nothing is known — which reads as "same zone only".
/// </summary>
public sealed class VendorLocations
{
    /// <summary>Level.Type of an event NPC placement.</summary>
    private const byte ENpcLevelType = 8;

    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly object _gate = new();
    private Task? _build;
    private volatile IReadOnlyDictionary<uint, NpcLocation>? _locations;

    public VendorLocations(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    /// <summary>What this is doing, or why it knows nothing.</summary>
    public string Status { get; private set; } = "Idle — vendor positions are read the first time a trip needs them.";

    /// <summary>True once positions are loaded (even if none were found).</summary>
    public bool Ready => _locations != null;

    /// <summary>Where an NPC stands; null while still loading, or when the data does not place them.</summary>
    public NpcLocation? Find(uint npcDataId)
    {
        EnsureStarted();
        return _locations != null && _locations.TryGetValue(npcDataId, out var location) ? location : null;
    }

    /// <summary>Start loading, if it has not been. Safe to call every frame.</summary>
    public void EnsureStarted()
    {
        if (_build != null)
            return;

        lock (_gate)
        {
            if (_build != null)
                return;

            Status = "Reading vendor positions from the game data…";
            _build = Task.Run(Build);
        }
    }

    private void Build()
    {
        var found = new Dictionary<uint, NpcLocation>();
        try
        {
            foreach (var level in _data.GetExcelSheet<Level>())
            {
                if (level.Type == ENpcLevelType && level.Object.RowId != 0 && level.Territory.RowId != 0)
                    found.TryAdd(level.Object.RowId, new NpcLocation(level.Territory.RowId, new Vector3(level.X, level.Y, level.Z)));
            }

            var fromSheet = found.Count;
            foreach (var territory in _data.GetExcelSheet<TerritoryType>())
            {
                var bg = territory.Bg.ExtractText();
                var cut = string.IsNullOrEmpty(bg) ? -1 : bg.IndexOf("/level/", StringComparison.Ordinal);
                if (cut < 0)
                    continue;

                // planevent.lgb ONLY — see the class comment before adding another layer file.
                var layout = _data.GetFile<LgbFile>($"bg/{bg[..(cut + 1)]}level/planevent.lgb");
                if (layout == null)
                    continue;

                foreach (var layer in layout.Layers)
                {
                    foreach (var placed in layer.InstanceObjects)
                    {
                        if (placed.AssetType != LayerEntryType.EventNPC || placed.Object is not LayerCommon.ENPCInstanceObject npc)
                            continue;

                        var at = placed.Transform.Translation;
                        found.TryAdd(npc.ParentData.ParentData.BaseId, new NpcLocation(territory.RowId, new Vector3(at.X, at.Y, at.Z)));
                    }
                }
            }

            Status = $"{found.Count:N0} NPC positions known ({fromSheet:N0} from the Level sheet, the rest from zone layouts).";
        }
        catch (Exception ex)
        {
            // Fail open: whatever was read so far still helps; an empty map just means same-zone only.
            _log.Warning(ex, "Hephaestus: reading vendor positions failed — vendor trips are limited to sellers already nearby");
            Status = $"Could not read vendor positions ({ex.GetType().Name}) — only sellers already nearby can be used.";
        }

        _locations = found;
    }
}
