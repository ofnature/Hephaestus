using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Hephaestus.Features.Materials;
using Aetheryte = Lumina.Excel.Sheets.Aetheryte;

namespace Hephaestus.Services.Game;

/// <summary>
/// How to get into a zone: from the <c>Aetheryte</c> sheet, the way Odysseus's AetheryteCatalog and
/// GameStepWorld.RouteTo do it (field-proven there). A teleport straight into the zone when it has
/// an attuned aetheryte (the nearest to where we are going); else a hop across the city aethernet —
/// half a city can hold no aetheryte at all (Ul'dah's Steps of Thal, Limsa's Upper Decks, Old
/// Gridania), and those are where the guild vendors stand; else no route.
///
/// Attunement is checked because an unattuned aetheryte is not a route, it is a refused teleport.
/// When it cannot be read at all, the candidate is taken and Lifestream gets to say no.
/// </summary>
public sealed unsafe class TravelCatalog
{
    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private List<Row>? _rows;
    private Dictionary<uint, byte>? _groupByTerritory;

    public TravelCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    private sealed record Row(uint Id, bool IsAetheryte, uint Territory, byte Group, uint PlaceNameId, Vector3? Position);

    /// <param name="currentTerritory">Where the character is: already on the right aethernet means one hop and no teleport.</param>
    public TravelRoute? RouteTo(uint territory, Vector3 near, uint currentTerritory)
    {
        EnsureLoaded();

        Row? direct = null;
        Row? shard = null;
        foreach (var row in _rows!)
        {
            if (row.Territory != territory)
                continue;

            if (row.IsAetheryte)
            {
                if (Attuned(row.Id) && Closer(row, direct, near))
                    direct = row;
            }
            else if (row.PlaceNameId != 0 && Closer(row, shard, near))
            {
                shard = row;
            }
        }

        if (direct != null)
            return new TravelRoute(direct.Id, territory, 0, territory);

        if (shard == null || shard.Group == 0)
            return null;

        if (_groupByTerritory!.TryGetValue(currentTerritory, out var here) && here == shard.Group)
            return new TravelRoute(0, currentTerritory, shard.PlaceNameId, territory);

        foreach (var row in _rows!)
        {
            if (row.IsAetheryte && row.Group == shard.Group && Attuned(row.Id))
                return new TravelRoute(row.Id, row.Territory, shard.PlaceNameId, territory);
        }

        return null;
    }

    /// <summary>What an in-zone hop costs in walking terms: reaching the shard, the loading screen, the menu.</summary>
    private const float HopCost = 30f;

    /// <summary>A hop must beat the direct walk by at least this, or it is not worth the loading screen.</summary>
    private const float MinSavings = 60f;

    /// <summary>
    /// A hop across THIS zone's aethernet, for a vendor far away but near a shard: the walk to the
    /// nearest shard here plus the walk from the shard by them must beat the direct walk by a margin
    /// worth a loading screen (GBR's VendorNavigator does the same sum). Null = just walk.
    /// </summary>
    public TravelRoute? HopWithin(uint territory, Vector3 from, Vector3 to)
    {
        EnsureLoaded();

        Row? nearFrom = null;
        Row? nearTo = null;
        var fromDistance = float.MaxValue;
        var toDistance = float.MaxValue;
        foreach (var row in _rows!)
        {
            if (row.Territory != territory || row.PlaceNameId == 0 || row.Position is not { } at)
                continue;

            var df = Vector3.Distance(at, from);
            if (df < fromDistance)
                (nearFrom, fromDistance) = (row, df);
            var dt = Vector3.Distance(at, to);
            if (dt < toDistance)
                (nearTo, toDistance) = (row, dt);
        }

        if (nearFrom == null || nearTo == null || nearFrom.Id == nearTo.Id)
            return null;

        var direct = Vector3.Distance(from, to);
        return fromDistance + toDistance + HopCost + MinSavings <= direct
            ? new TravelRoute(0, territory, nearTo.PlaceNameId, territory)
            : null;
    }

    private static bool Closer(Row row, Row? best, Vector3 near)
    {
        if (best == null)
            return true;

        var mine = row.Position is { } a ? Vector3.Distance(a, near) : float.MaxValue;
        var theirs = best.Position is { } b ? Vector3.Distance(b, near) : float.MaxValue;
        return mine < theirs;
    }

    /// <summary>The character has attuned to this aetheryte (true when the unlock state cannot be read: Lifestream gets to say no).</summary>
    public bool IsAttuned(uint aetheryteId) => Attuned(aetheryteId);

    private static bool Attuned(uint aetheryteId)
    {
        try
        {
            var state = UIState.Instance();
            return state == null || state->IsAetheryteUnlocked(aetheryteId);
        }
        catch
        {
            return true;
        }
    }

    private void EnsureLoaded()
    {
        if (_rows != null)
            return;

        _rows = new List<Row>();
        _groupByTerritory = new Dictionary<uint, byte>();
        try
        {
            foreach (var aetheryte in _data.GetExcelSheet<Aetheryte>())
            {
                var territory = aetheryte.Territory.RowId;
                if (aetheryte.RowId == 0 || territory == 0)
                    continue;

                _groupByTerritory.TryAdd(territory, aetheryte.AethernetGroup);
                _rows.Add(new Row(
                    aetheryte.RowId, aetheryte.IsAetheryte, territory, aetheryte.AethernetGroup, aetheryte.AethernetName.RowId, PositionOf(aetheryte)));
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Hephaestus: could not read the aetheryte sheet — no teleports to vendors");
            _rows.Clear();
        }
    }

    private static Vector3? PositionOf(Aetheryte aetheryte)
    {
        try
        {
            foreach (var level in aetheryte.Level)
            {
                if (level.RowId != 0 && level.ValueNullable is { } at)
                    return new Vector3(at.X, at.Y, at.Z);
            }
        }
        catch
        {
            // A missing Level row is a missing position, not a missing aetheryte.
        }

        return null;
    }
}
