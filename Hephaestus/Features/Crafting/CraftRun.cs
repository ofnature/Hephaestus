using System;

namespace Hephaestus.Features.Crafting;

/// <summary>What the game looks like right now, as far as a craft run cares. Built each tick by the adapter.</summary>
/// <param name="LogOpen">The crafting log (RecipeNote) is visible and ready.</param>
/// <param name="SelectedRecipeId">Recipe selected in the log; 0 when none or its data is not ready.</param>
/// <param name="Craftable">How many of the selected recipe the materials AND crystals in the bags make; -1 when unreadable.</param>
/// <param name="IngredientsAssigned">Every ingredient of the selected recipe has its full NQ+HQ amount assigned.</param>
/// <param name="SynthesisOpen">The Synthesis window exists — a craft is in progress.</param>
/// <param name="Transitioning">ConditionFlag.ExecutingCraftingAction — start/finish animation or an action resolving.</param>
/// <param name="Progress">Valid while <paramref name="SynthesisOpen"/>.</param>
/// <param name="MaxProgress">Valid while <paramref name="SynthesisOpen"/>.</param>
/// <param name="Durability">Valid while <paramref name="SynthesisOpen"/>.</param>
/// <param name="ResultItemCount">NQ+HQ count of the run's result item in the bags.</param>
/// <param name="MinGearConditionPercent">Lowest condition % across equipped gear.</param>
/// <param name="GearMaterialUnselected">A piece of GEAR the recipe uses up has not been chosen yet (the log's "Unselected" row).</param>
/// <param name="GearMaterialRefusal">Why choosing it is not safe (see <see cref="GearMaterial"/>); null when it is.</param>
/// <param name="ResultItemHqCount">HQ-only count of the run's result item — how completion tells an HQ result from NQ.</param>
public readonly record struct CraftRunObservation(
    bool LogOpen,
    uint SelectedRecipeId,
    int Craftable,
    bool IngredientsAssigned,
    bool SynthesisOpen,
    bool Transitioning,
    int Progress,
    int MaxProgress,
    int Durability,
    int ResultItemCount,
    int MinGearConditionPercent,
    bool GearMaterialUnselected = false,
    string? GearMaterialRefusal = null,
    int ResultItemHqCount = 0);

/// <summary>The one thing the adapter should do this tick.</summary>
public enum CraftRunCommand
{
    None,
    OpenRecipe,
    AssignIngredients,

    /// <summary>Choose the copy of a piece of gear the recipe uses up: open the row's picker, then take an entry.</summary>
    ChooseGearMaterial,
    PressSynthesize,
}

public enum CraftRunPhase
{
    /// <summary>Not running: never started, finished, or halted (see <see cref="CraftRun.CanResume"/>).</summary>
    Idle,

    /// <summary>In the log, getting the next craft ready: recipe selected, materials assigned.</summary>
    Preparing,

    /// <summary>Synthesize pressed; waiting for the Synthesis window.</summary>
    Starting,

    /// <summary>A craft is in progress — the per-step action driver owns this phase.</summary>
    Crafting,

    /// <summary>Synthesis window gone; waiting for the finish animation and the result item.</summary>
    Finishing,
}

/// <summary>
/// Why a run stopped short, for whoever is sequencing runs (the queue): some of these mean "this
/// ITEM cannot be made, move on", others mean "something needs a person, stop everything".
/// </summary>
public enum CraftRunHalt
{
    /// <summary>Not halted: never started, still running, or finished everything asked of it.</summary>
    None,

    /// <summary>Stopped by the player.</summary>
    Stopped,

    /// <summary>A synthesis was cancelled in game.</summary>
    Cancelled,

    /// <summary>Gear condition is below the gate — needs a repair.</summary>
    GearGate,

    /// <summary>The crafting log says nothing more can be made from the materials on hand.</summary>
    OutOfMaterials,

    /// <summary>The synthesis failed more times in a row than the retry cap allows.</summary>
    FailedRepeatedly,

    /// <summary>Synthesize was pressed but no synthesis began (stats too low, recipe requirements not met, ...).</summary>
    WouldNotStart,

    /// <summary>The crafting log would not open the recipe or take the materials.</summary>
    LogUnresponsive,

    /// <summary>A piece of gear the recipe uses up cannot safely be chosen for the player (materia on it, a gearset's last copy).</summary>
    MaterialChoice,

    InternalError,
}

/// <summary>Immutable settings snapshot taken when a run starts or resumes.</summary>
public readonly record struct CraftRunSettings(int MaxCraftRetries, int GearConditionThresholdPercent);

/// <summary>
/// The OUTER loop of crafting: make one recipe N times from the crafting log. Pure decision layer —
/// it is handed an observation and answers with at most one command; the adapter executes it.
///
/// Modelled on how Artisan's Endurance/"Craft X" drives repeat crafts (select recipe, assign
/// ingredients, fire Synthesize, wait for the craft, count down), with two deliberate differences:
/// completion is VERIFIED by the result item arriving in the bags rather than assumed from the
/// craft ending, and a halted run keeps its position so it can be resumed.
///
/// The INNER loop (choosing and pressing actions during <see cref="CraftRunPhase.Crafting"/>) is
/// not here: it belongs to the solver's action driver. Until that exists the player, or an in-game
/// macro, performs the actions and this loop starts each next craft.
/// </summary>
public sealed class CraftRun
{
    /// <summary>Minimum gap between commands — the log needs a moment to react to each one.</summary>
    public static readonly TimeSpan CommandPacing = TimeSpan.FromMilliseconds(500);

    /// <summary>How long Synthesize may take to produce the Synthesis window.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>After control returns, how long the result item may take to land in the bags.</summary>
    public static readonly TimeSpan ResultSettle = TimeSpan.FromSeconds(3);

    /// <summary>Failed starts in a row before giving up (same threshold Artisan uses).</summary>
    public const int MaxFailedStarts = 5;

    /// <summary>The same command issued this many times with nothing changing means we are stuck.</summary>
    public const int MaxRepeats = 8;

    private CraftRunSettings _settings;
    private DateTime _nextCommandAt;
    private DateTime _deadline;
    private DateTime _settleDeadline;
    private CraftRunCommand _lastCommand;
    private int _repeats;
    private int _failedStarts;
    private int _consecutiveFailures;
    private int _countBefore;
    private int _hqBefore;
    private int _lastProgress;
    private int _lastMaxProgress;
    private int _lastDurability;
    private string? _stopAfterCraft; // the reason, once someone asked to stop after the synthesis in progress

    public CraftRunPhase Phase { get; private set; } = CraftRunPhase.Idle;

    public uint RecipeId { get; private set; }

    public int Requested { get; private set; }

    public int Completed { get; private set; }

    public int Failed { get; private set; }

    /// <summary>What the run is doing, or why it is not doing anything.</summary>
    public string Status { get; private set; } = "Idle — no run started.";

    /// <summary>Why the run halted; <see cref="CraftRunHalt.None"/> while running or after finishing.</summary>
    public CraftRunHalt HaltKind { get; private set; }

    /// <summary>Fired when a craft completes successfully: the recipe, and whether the result was HQ.</summary>
    public event Action<uint, bool>? CraftCompleted;

    /// <summary>The same, in words, without the position; empty when not halted.</summary>
    public string HaltReason { get; private set; } = string.Empty;

    /// <summary>True while the run is driving the character (this is what Hephaestus.IsBusy reports).</summary>
    public bool Active => Phase != CraftRunPhase.Idle;

    /// <summary>A halted run with crafts left: <see cref="Resume"/> continues where it stopped.</summary>
    public bool CanResume => Phase == CraftRunPhase.Idle && RecipeId != 0 && Completed < Requested;

    public void Start(uint recipeId, int count, CraftRunSettings settings)
    {
        if (recipeId == 0 || count <= 0)
            return;

        RecipeId = recipeId;
        Requested = count;
        Completed = 0;
        Failed = 0;
        Begin(settings);
    }

    public void Resume(CraftRunSettings settings)
    {
        if (CanResume)
            Begin(settings);
    }

    /// <summary>Stop now, at the player's request. The position is kept, so the run can be resumed.</summary>
    public void Halt(string reason) => Halt(CraftRunHalt.Stopped, reason);

    /// <summary>
    /// Stop once the synthesis in progress is over — never abandon one — and start no other. Halts as
    /// <see cref="CraftRunHalt.Stopped"/> (resumable) the next time the run would prepare a craft; a run
    /// whose last craft that was simply finishes. Idle: nothing to do. A fresh start or resume forgets it.
    /// </summary>
    public void StopAfterCraft(string reason)
    {
        if (Active)
            _stopAfterCraft = reason;
    }

    /// <summary>Stop now. The position is kept, so the run can be resumed.</summary>
    public void Halt(CraftRunHalt kind, string reason)
    {
        if (!Active)
            return;

        HaltKind = kind;
        HaltReason = reason;
        Phase = CraftRunPhase.Idle;
        Status = $"Halted at {Completed} / {Requested} — {reason}";
    }

    /// <summary>Forget the run entirely.</summary>
    public void Clear()
    {
        Phase = CraftRunPhase.Idle;
        RecipeId = 0;
        Requested = 0;
        Completed = 0;
        Failed = 0;
        HaltKind = CraftRunHalt.None;
        HaltReason = string.Empty;
        Status = "Idle — no run started.";
    }

    public CraftRunCommand Tick(in CraftRunObservation obs, DateTime now) => Phase switch
    {
        CraftRunPhase.Preparing => TickPreparing(obs, now),
        CraftRunPhase.Starting => TickStarting(obs, now),
        CraftRunPhase.Crafting => TickCrafting(obs, now),
        CraftRunPhase.Finishing => TickFinishing(obs, now),
        _ => CraftRunCommand.None,
    };

    private void Begin(CraftRunSettings settings)
    {
        _settings = settings;
        HaltKind = CraftRunHalt.None;
        HaltReason = string.Empty;
        _nextCommandAt = default;
        _failedStarts = 0;
        _consecutiveFailures = 0;
        _stopAfterCraft = null;
        EnterPreparing();
    }

    private void EnterPreparing()
    {
        Phase = CraftRunPhase.Preparing;
        _lastCommand = CraftRunCommand.None;
        _repeats = 0;
        Status = $"Preparing craft {Completed + 1} / {Requested}";
    }

    private CraftRunCommand TickPreparing(in CraftRunObservation obs, DateTime now)
    {
        // A craft is already under way (started by hand, or the plugin reloaded mid-craft): adopt it.
        if (obs.SynthesisOpen)
        {
            _countBefore = obs.ResultItemCount;
            _hqBefore = obs.ResultItemHqCount;
            EnterCrafting(obs);
            return CraftRunCommand.None;
        }

        if (obs.Transitioning)
            return CraftRunCommand.None;

        if (Completed >= Requested)
        {
            Phase = CraftRunPhase.Idle;
            Status = Failed == 0
                ? $"Done — crafted {Completed}."
                : $"Done — crafted {Completed}, {Failed} failed along the way.";
            return CraftRunCommand.None;
        }

        // Asked to stop after the craft that was in progress: this is the point the next one would begin.
        if (_stopAfterCraft is { } stopReason)
        {
            Halt(CraftRunHalt.Stopped, stopReason);
            return CraftRunCommand.None;
        }

        // Pre-craft gear gate: broken gear gives no stats, so crafting on it just fails repeatedly.
        if (obs.MinGearConditionPercent < _settings.GearConditionThresholdPercent)
        {
            Halt(CraftRunHalt.GearGate, $"gear is at {obs.MinGearConditionPercent}% (gate {_settings.GearConditionThresholdPercent}%) — repair, then resume");
            return CraftRunCommand.None;
        }

        if (now < _nextCommandAt)
            return CraftRunCommand.None;

        if (!obs.LogOpen || obs.SelectedRecipeId != RecipeId)
            return Issue(CraftRunCommand.OpenRecipe, now);

        if (obs.Craftable < 0)
        {
            Status = "Waiting for the crafting log to report how many can be made";
            return CraftRunCommand.None;
        }

        if (obs.Craftable == 0)
        {
            Halt(CraftRunHalt.OutOfMaterials, "out of materials");
            return CraftRunCommand.None;
        }

        // Gear used up as a material is one particular item, not a count: the log wants it chosen. Only
        // when losing ANY offered copy is safe; otherwise this item waits for a person.
        if (obs.GearMaterialUnselected)
        {
            if (obs.GearMaterialRefusal is { } refusal)
            {
                Halt(CraftRunHalt.MaterialChoice, refusal);
                return CraftRunCommand.None;
            }

            return Issue(CraftRunCommand.ChooseGearMaterial, now);
        }

        if (!obs.IngredientsAssigned)
            return Issue(CraftRunCommand.AssignIngredients, now);

        var command = Issue(CraftRunCommand.PressSynthesize, now);
        if (command == CraftRunCommand.PressSynthesize)
        {
            _countBefore = obs.ResultItemCount;
            _hqBefore = obs.ResultItemHqCount;
            _deadline = now + StartTimeout;
            Phase = CraftRunPhase.Starting;
            Status = $"Starting craft {Completed + 1} / {Requested}";
        }

        return command;
    }

    private CraftRunCommand TickStarting(in CraftRunObservation obs, DateTime now)
    {
        if (obs.SynthesisOpen)
        {
            _failedStarts = 0;
            EnterCrafting(obs);
        }
        else if (now > _deadline && !obs.Transitioning)
        {
            if (++_failedStarts >= MaxFailedStarts)
                Halt(CraftRunHalt.WouldNotStart, "the craft would not start");
            else
                EnterPreparing();
        }

        return CraftRunCommand.None;
    }

    private void EnterCrafting(in CraftRunObservation obs)
    {
        Phase = CraftRunPhase.Crafting;
        _lastProgress = obs.Progress;
        _lastMaxProgress = obs.MaxProgress;
        _lastDurability = obs.Durability;
        _settleDeadline = default;
        Status = $"Crafting {Completed + 1} / {Requested}";
    }

    private CraftRunCommand TickCrafting(in CraftRunObservation obs, DateTime now)
    {
        if (obs.SynthesisOpen)
        {
            _lastProgress = obs.Progress;
            _lastMaxProgress = obs.MaxProgress;
            _lastDurability = obs.Durability;
        }
        else
        {
            Phase = CraftRunPhase.Finishing;
            Status = $"Finishing craft {Completed + 1} / {Requested}";
            return TickFinishing(obs, now);
        }

        return CraftRunCommand.None;
    }

    private CraftRunCommand TickFinishing(in CraftRunObservation obs, DateTime now)
    {
        if (obs.Transitioning)
            return CraftRunCommand.None;

        // The verdict is the item itself, not the craft having ended.
        if (obs.ResultItemCount > _countBefore)
        {
            Completed++;
            _consecutiveFailures = 0;
            CraftCompleted?.Invoke(RecipeId, obs.ResultItemHqCount > _hqBefore);
            EnterPreparing();
            return CraftRunCommand.None;
        }

        if (_settleDeadline == default)
            _settleDeadline = now + ResultSettle;
        if (now < _settleDeadline)
            return CraftRunCommand.None;

        // No item arrived. Durability ran out short of full progress = the synthesis failed and
        // the materials are gone. Anything else = someone cancelled it.
        var failed = _lastDurability <= 0 && _lastProgress < _lastMaxProgress;
        if (!failed)
        {
            Halt(CraftRunHalt.Cancelled, "the craft was cancelled");
            return CraftRunCommand.None;
        }

        Failed++;
        if (++_consecutiveFailures > _settings.MaxCraftRetries)
            Halt(CraftRunHalt.FailedRepeatedly, $"failed {_consecutiveFailures} times in a row");
        else
            EnterPreparing();

        return CraftRunCommand.None;
    }

    /// <summary>Pace commands, and notice when the same one keeps having no effect.</summary>
    private CraftRunCommand Issue(CraftRunCommand command, DateTime now)
    {
        _repeats = command == _lastCommand ? _repeats + 1 : 1;
        _lastCommand = command;

        if (_repeats > MaxRepeats)
        {
            Halt(CraftRunHalt.LogUnresponsive, command switch
            {
                CraftRunCommand.OpenRecipe => "could not open the recipe in the crafting log",
                CraftRunCommand.AssignIngredients => "could not assign materials",
                CraftRunCommand.ChooseGearMaterial => "could not choose the gear it uses up as a material — the log's picker did not take it",
                _ => "the crafting log is not responding",
            });
            return CraftRunCommand.None;
        }

        _nextCommandAt = now + CommandPacing;
        Status = command switch
        {
            CraftRunCommand.OpenRecipe => "Opening the recipe",
            CraftRunCommand.AssignIngredients => "Assigning materials",
            CraftRunCommand.ChooseGearMaterial => "Choosing the gear it uses up as a material",
            _ => Status,
        };
        return command;
    }
}
