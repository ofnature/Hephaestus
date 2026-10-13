using System;

namespace Hephaestus.Features.Crafting;

/// <summary>
/// The "how many?" and the start/resume decision, shared by every place a run can be launched from
/// (the button inside the crafting log, and the side window) so they can never disagree.
/// </summary>
public sealed class CraftRunLauncher
{
    private readonly CraftRun _run;
    private readonly Func<CraftRunSettings> _settings;
    private readonly Func<bool>? _blocked;
    private readonly Func<uint, int, bool>? _viaJobChange;

    /// <param name="blocked">Something else is sequencing craft runs right now (the queue) — a manual start would fight it.</param>
    /// <param name="viaJobChange">
    /// Offered every start first (recipe, count). True = taken: the recipe belongs to a job the character
    /// is not on, and whoever CAN change job (the queue engine) will run it. False, or absent = ours to start.
    /// </param>
    public CraftRunLauncher(
        CraftRun run, Func<CraftRunSettings> settings, Func<bool>? blocked = null, Func<uint, int, bool>? viaJobChange = null)
    {
        _run = run;
        _settings = settings;
        _blocked = blocked;
        _viaJobChange = viaJobChange;
    }

    /// <summary>The queue (or another plugin's request) has the character; manual starts are off.</summary>
    public bool Blocked => _blocked?.Invoke() == true;

    /// <summary>How many to craft; 0 = everything the materials on hand allow ("Craft All").</summary>
    public int Count { get; set; }

    /// <summary>
    /// The number a run would be started for: the count asked for, but never more than the materials
    /// and crystals on hand make. (Wanting more than that is what the queue is for.)
    /// </summary>
    public int Target(int craftable)
    {
        var max = Math.Max(0, craftable);
        return Count > 0 ? Math.Min(Count, max) : max;
    }

    /// <summary>Always says the max, so a count that was cut down — or a button that is off — explains itself.</summary>
    public string Label(int craftable)
        => Count > 0 ? $"Craft {Target(craftable)} (max {Math.Max(0, craftable)})" : $"Craft All ({Math.Max(0, craftable)})";

    /// <summary>A fresh run can start: nothing running or waiting to resume, a recipe selected, something to make.</summary>
    public bool CanStart(uint selectedRecipeId, int craftable)
        => !Blocked && !_run.Active && !_run.CanResume && selectedRecipeId != 0 && Target(craftable) > 0;

    public void Start(uint selectedRecipeId, int craftable)
    {
        if (!CanStart(selectedRecipeId, craftable))
            return;

        // Never press Synthesize on the wrong job: the game refuses it, and the run would only time out.
        if (_viaJobChange?.Invoke(selectedRecipeId, Target(craftable)) == true)
            return;

        _run.Start(selectedRecipeId, Target(craftable), _settings());
    }

    public void Resume()
    {
        if (!Blocked)
            _run.Resume(_settings());
    }
}
