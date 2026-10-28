using System.Collections.Generic;
using Dalamud.Configuration;

namespace Hephaestus;

/// <summary>How crafting actions get executed once the solver has picked one.</summary>
public enum ExecutionMode
{
    /// <summary>The plugin shows the recommended action; the player presses it.</summary>
    Assisted = 0,

    /// <summary>The plugin presses the action itself.</summary>
    Auto = 1,
}

/// <summary>Which main-window page opens first when the window is opened.</summary>
public enum MainPage
{
    Overview = 0,
    Queue = 1,
    BuyList = 2,
    GatherList = 3,
    Settings = 4,
    Debug = 5,
}

/// <summary>
/// Root persistent configuration for Hephaestus.
///
/// This file is ONE JSON shared by every client launched from the same XIVLauncher (last save
/// wins), so nothing in here is per-box by accident: anything per-character lives in
/// <see cref="Characters"/> keyed by content id, and bulk per-character data (the craft queue) is
/// not in here at all — see <see cref="Features.Queue.CraftListStore"/>.
/// </summary>
public sealed class HephaestusConfig : IPluginConfiguration
{
    /// <summary>Config schema version, bumped when a load-time migration is needed.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Newest schema version — a config below this runs <see cref="Migrate"/> on load.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Bring a loaded config up to <see cref="CurrentVersion"/>. Returns true when something
    /// changed (the caller then saves). A saved config OVERRIDES a property's C# default, so any
    /// future default change needs a step here (same rule as CharonConfig.Migrate).
    /// </summary>
    public bool Migrate()
    {
        if (Version >= CurrentVersion)
            return false;

        Version = CurrentVersion;
        return true;
    }

    /// <summary>Mode for every character without an override. Auto: multibox-first, least hands-on.</summary>
    public ExecutionMode DefaultMode { get; set; } = ExecutionMode.Auto;

    /// <summary>The page the main window opens on. Changeable in Settings → Window.</summary>
    public MainPage LandingPage { get; set; } = MainPage.Overview;

    /// <summary>Per-character settings keyed by content id. No entry = follows the globals.</summary>
    public Dictionary<ulong, CharacterSettings> Characters { get; set; } = new();

    /// <summary>
    /// Per-recipe options set from the crafting-log window, keyed by recipe id. Character-agnostic
    /// on purpose: a recipe wants the same food and solver options on every box. An entry is only
    /// created when something is changed from the defaults.
    /// </summary>
    public Dictionary<uint, RecipeSettings> Recipes { get; set; } = new();

    public QueueSettings Queue { get; set; } = new();

    public SolverTuning Solver { get; set; } = new();

    /// <summary>The mode a character actually runs in: its override, else <see cref="DefaultMode"/>.</summary>
    public ExecutionMode ModeFor(ulong contentId)
        => Characters.TryGetValue(contentId, out var character) && character.ModeOverride is { } mode
            ? mode
            : DefaultMode;

    /// <summary>
    /// Does the queue buy (NPC vendors) and gather (Demeter) on its own when it runs short? Per
    /// character; unset follows the account type, read live: a free trial has nothing else to use and
    /// is fully automated, a paid account may want to buy some things on the market board itself first
    /// (user, 2026-09-21) and uses the Buy/Gather List buttons. The market board is NEVER automated.
    /// </summary>
    public bool AutoSourcingFor(ulong contentId, bool freeTrial)
        => Characters.TryGetValue(contentId, out var character) && character.AutoSourcing is { } chosen ? chosen : freeTrial;

    /// <summary>Get-or-create the settings entry for a character (content id 0 = not logged in).</summary>
    public CharacterSettings CharacterFor(ulong contentId)
    {
        if (!Characters.TryGetValue(contentId, out var character))
        {
            character = new CharacterSettings();
            Characters[contentId] = character;
        }

        return character;
    }
}

public sealed class CharacterSettings
{
    /// <summary>Null = follow <see cref="HephaestusConfig.DefaultMode"/>.</summary>
    public ExecutionMode? ModeOverride { get; set; }

    /// <summary>Null = follow the account type (see <see cref="HephaestusConfig.AutoSourcingFor"/>).</summary>
    public bool? AutoSourcing { get; set; }
}

public sealed class RecipeSettings
{
    /// <summary>Food to keep up while crafting this recipe; 0 = none.</summary>
    public uint FoodItemId { get; set; }

    public bool FoodHq { get; set; }

    /// <summary>Medicine to keep up while crafting this recipe; 0 = none.</summary>
    public uint MedicineItemId { get; set; }

    public bool MedicineHq { get; set; }

    /// <summary>Raphael: only use Progress actions at the end of the rotation.</summary>
    public bool BackloadProgress { get; set; }

    /// <summary>Raphael: allow Heart and Soul (specialist, consumes a delineation).</summary>
    public bool AllowHeartAndSoul { get; set; }

    /// <summary>Raphael: allow Quick Innovation (specialist, consumes a delineation).</summary>
    public bool AllowQuickInnovation { get; set; }
}

public sealed class QueueSettings
{
    /// <summary>
    /// Retries after a FAILED synthesis (materials lost) before the item is skipped + flagged.
    /// Capped so a bad streak cannot burn a whole stack of materials. Can't-start conditions
    /// (missing mats, recipe locked, no gearset) never retry — they skip + flag immediately.
    /// </summary>
    public int MaxCraftRetries { get; set; } = 2;

    /// <summary>
    /// Pre-craft gear gate. Broken gear contributes no stats, so crafting on it fails repeatedly;
    /// when any equipped piece is below this condition % the queue repairs (if enabled and
    /// possible) or PAUSES + flags before starting the next craft. Paused, not skipped: the queue
    /// resumes where it stopped.
    /// </summary>
    public int GearConditionThresholdPercent { get; set; } = 20;

    /// <summary>
    /// Craft missing intermediates automatically, before the entries that need them. Off: every entry
    /// is crafted exactly as listed, and one that runs out of materials is flagged.
    /// </summary>
    public bool ExpandSubCrafts { get; set; } = true;

    /// <summary>Offer "Add to Hephaestus Queue" on the game's right-click menus, for craftable items.</summary>
    public bool ContextMenu { get; set; } = true;

    /// <summary>Self-repair with Dark Matter, else a same-zone mender; pause only if neither works.</summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>Re-apply the list's food/potion before a craft the buff would not outlast.</summary>
    public bool FoodPotionUpkeep { get; set; } = true;

    /// <summary>Extract materia from fully spiritbonded gear between crafts.</summary>
    public bool MateriaExtraction { get; set; } = true;
}

public sealed class SolverTuning
{
    /// <summary>
    /// Master switch for the solver during a synthesis. Off = Hephaestus neither recommends nor
    /// presses anything (Craft All still starts each craft; you or a macro do the actions).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Assisted mode: outline the recommended action's slot on your own hotbars.</summary>
    public bool HighlightHotbar { get; set; } = true;

    /// <summary>
    /// When the queue starts or lands on a job, solve the Raphael baseline for that job's coming
    /// crafts in the background (one at a time, never during a synthesis), so the first craft of
    /// each recipe does not wait on the solver. The same total work as solving at craft start, earlier.
    /// </summary>
    public bool PreSolveQueue { get; set; } = true;

    /// <summary>
    /// Threads for the Raphael baseline solve. 0 = half the machine's cores: several game clients
    /// may be solving at the same moment, and none of them should starve the game.
    /// </summary>
    public int RaphaelThreads { get; set; }

    /// <summary>
    /// Wall-clock budget for the per-step lookahead. It runs on a background thread over an
    /// immutable state snapshot during the action's animation lock, so this bounds decision
    /// latency — it is not a per-frame cost.
    /// </summary>
    public int LookaheadBudgetMs { get; set; } = 400;

    /// <summary>Hard depth cap for the iterative-deepening lookahead.</summary>
    public int LookaheadMaxDepth { get; set; } = 4;

    /// <summary>
    /// Early-discard rule for the Raphael baseline: drift (CP, durability or progress) beyond this
    /// % from the baseline's planned trajectory drops it. The hard rule is separate and not
    /// tunable: the baseline is always dropped once replaying its remaining actions from the real
    /// state can no longer complete the craft.
    /// </summary>
    public float BaselineDeviationPercent { get; set; } = 25f;
}
