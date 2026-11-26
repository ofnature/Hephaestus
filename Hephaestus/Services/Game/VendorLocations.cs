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
/// THE SELLERS NEITHER SOURCE PLACES (<see cref="ReadOutOfProcess"/>, 2026-11-26): the 16 ingredients left over
/// were all sold by NPCs of the starting cities and the Shroud that stand in <c>planner.lgb</c>. Their positions
/// are a TABLE here, read ONCE, outside the game, from those nine zones' <c>planner.lgb</c> under a heap limit
/// (they parse cleanly: 98 MB) - so the file is still never opened inside the game. Which NPCs to look for came
/// from Item Vendor Location's hand-kept list (the user's question, "anything we can borrow from this?"); the
/// positions are the game's own, with their height, and they differ from that list in four places (it has the
/// Calamity salvagers and recompense officers of Limsa and Gridania in each other's city). Our own two sources
/// win where they know an NPC. The probe: <c>.build/vendor-probe</c> (not in git).
///
/// Built once, on a background task (0.6 s would hitch the game; it reads data files, no game
/// state). Until it finishes, and if it fails, nothing is known — which reads as "same zone only".
/// </summary>
public sealed class VendorLocations
{
    /// <summary>Level.Type of an event NPC placement.</summary>
    private const byte ENpcLevelType = 8;

    /// <summary>
    /// NPCs that stand in <c>planner.lgb</c>: (ENpc id, territory, x, y, z), read out of process from game
    /// version 2026.11.03. See the class comment before adding to it - never by reading that file in game.
    /// </summary>
    public static readonly (uint Npc, uint Territory, float X, float Y, float Z)[] ReadOutOfProcess =
    {
        (1000996u, 128u, -28.13f, 42.47f, 196.7f), // Smydhaemr, Limsa Lominsa Upper Decks
        (1000999u, 128u, -29.47f, 42.5f, 197.95f), // Soemrwyb, Limsa Lominsa Upper Decks
        (1001016u, 128u, -42.66f, 40f, 119.93f), // Holasfhis, Limsa Lominsa Upper Decks
        (1002389u, 128u, 95.84f, 40.25f, 67.63f), // OIC quartermaster, Limsa Lominsa Upper Decks
        (1006005u, 128u, 5.37f, 44.5f, 155.24f), // Calamity salvager, Limsa Lominsa Upper Decks
        (1017614u, 128u, 2.85f, 44.5f, 153.54f), // recompense officer, Limsa Lominsa Upper Decks
        (1003252u, 129u, -139.54f, 18.17f, 32f), // Blue Lily, Limsa Lominsa Lower Decks
        (1003633u, 129u, -259.3f, 16.2f, 37.5f), // scrip exchange, Limsa Lominsa Lower Decks
        (1005422u, 129u, -397.63f, 3.1f, 81.01f), // merchant & mender, Limsa Lominsa Lower Decks
        (1000185u, 132u, -8.59f, -0.02f, -2.21f), // Tanie, New Gridania
        (1000392u, 132u, -17.72f, -3.25f, 43.35f), // Ferreol, New Gridania
        (1000215u, 133u, 155.35f, 15.67f, -70.24f), // Domitien, Old Gridania
        (1000217u, 133u, 170.31f, 15.67f, -73.17f), // Geraint, Old Gridania
        (1000232u, 133u, 164.75f, 16.13f, -133.68f), // Sasamero, Old Gridania
        (1000267u, 133u, 103.92f, 12.95f, -213.02f), // Leopold, Old Gridania
        (1000301u, 133u, -87.17f, 7f, -173.48f), // Littlejohn, Old Gridania
        (1000391u, 133u, 66.82f, 8f, -143.45f), // Osgyth, Old Gridania
        (1003077u, 133u, 145.85f, 13.65f, -106.75f), // scrip exchange, Old Gridania
        (1006004u, 133u, -61.22f, 6.89f, -141.31f), // Calamity salvager, Old Gridania
        (1008145u, 133u, -31.25f, 10.13f, -245.37f), // Jonathas, Old Gridania
        (1017613u, 133u, -58.77f, 6.8f, -142.11f), // recompense officer, Old Gridania
        (1000220u, 148u, 16.2f, -8.04f, -15.61f), // merchant & mender, Central Shroud
        (1000396u, 148u, 82.61f, -7.9f, -103.32f), // merchant & mender, Central Shroud
        (1000717u, 148u, 175.65f, -32f, 319.34f), // merchant & mender, Central Shroud
        (1000222u, 152u, -213.93f, 2.33f, 300.44f), // merchant & mender, East Shroud
        (1000535u, 152u, -579.38f, 12.56f, 104.34f), // merchant & mender, East Shroud
        (1000579u, 152u, 16.04f, -4.59f, 220.51f), // encampment clothier & tailor, East Shroud
        (1002371u, 152u, -480.9f, 8.03f, 201.94f), // merchant & mender, East Shroud
        (1000597u, 153u, -163.07f, 9.84f, -78.63f), // arms supplier & mender, South Shroud
        (1002374u, 153u, 204.4f, 9.74f, -65.73f), // arms supplier, South Shroud
        (1002375u, 153u, 203.77f, 9.72f, -64.55f), // traveling armorer, South Shroud
        (1000230u, 154u, 421.5f, -3.82f, -125.96f), // tool supplier & mender, North Shroud
        (1000234u, 154u, 423.7f, -3.82f, -122.06f), // hamlet smithy, North Shroud
        (1000244u, 154u, 423.18f, -3.82f, -119.95f), // hamlet armorer, North Shroud
        (1000718u, 154u, 332.23f, -5.55f, 332.48f), // merchant & mender, North Shroud
        (1002376u, 154u, 10.64f, -44.73f, 220.22f), // merchant & mender, North Shroud
        (1002377u, 154u, 11.09f, -44.73f, 221.58f), // encampment clothier, North Shroud
    };

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

            // Last, so that what the game's own two sources say always wins.
            foreach (var (npc, territory, x, y, z) in ReadOutOfProcess)
                found.TryAdd(npc, new NpcLocation(territory, new Vector3(x, y, z)));

            Status = $"{found.Count:N0} NPC positions known ({fromSheet:N0} from the Level sheet, the rest from zone layouts and a table of {ReadOutOfProcess.Length}).";
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
