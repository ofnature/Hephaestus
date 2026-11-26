using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace Hephaestus.Services.Game;

/// <summary>
/// The special merchants, for the Vendor Location window: <see cref="SpecialShopIndex"/> built ONCE on a
/// background task the first time a right-click menu opens (it walks every NPC and shop sheet - too much for a
/// frame; it reads data files, no game state). Until it is done, and if it fails, nobody special is known.
/// SHOWING ONLY - see <see cref="SpecialShopIndex"/>.
/// </summary>
public sealed class SpecialVendorCatalog
{
    private static readonly IReadOnlyList<SpecialSeller> None = Array.Empty<SpecialSeller>();

    private readonly IDataManager _data;
    private readonly IPluginLog _log;
    private readonly object _gate = new();
    private Task? _build;
    private volatile Dictionary<uint, List<SpecialSeller>>? _sellers;

    public SpecialVendorCatalog(IDataManager data, IPluginLog log)
    {
        _data = data;
        _log = log;
    }

    /// <summary>What this is doing, or why it knows nothing.</summary>
    public string Status { get; private set; } = "Idle — special merchants are read the first time a right-click menu opens.";

    /// <summary>True once the sheets are read (even if nothing was found).</summary>
    public bool Ready => _sellers != null;

    /// <summary>Start reading, if it has not been. Safe to call every frame.</summary>
    public void EnsureStarted()
    {
        if (_build != null)
            return;

        lock (_gate)
        {
            if (_build != null)
                return;

            Status = "Reading special merchants from the game data…";
            _build = Task.Run(Build);
        }
    }

    /// <summary>Everyone who sells an item this way; empty while still reading.</summary>
    public IReadOnlyList<SpecialSeller> SellersOf(uint itemId)
        => _sellers != null && _sellers.TryGetValue(itemId, out var sellers) ? sellers : None;

    private void Build()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var found = SpecialShopIndex.Build(_data.Excel);
            Status = $"{found.Count:N0} items sold by special merchants (read in {clock.Elapsed.TotalSeconds:F1} s).";
            _sellers = found;
        }
        catch (Exception ex)
        {
            // Fail open: the window still lists the gil vendors.
            _log.Warning(ex, "Hephaestus: reading special merchants failed — the Vendor Location window lists gil vendors only");
            Status = $"Could not read special merchants ({ex.GetType().Name}) — gil vendors only.";
            _sellers = new Dictionary<uint, List<SpecialSeller>>();
        }
    }
}
