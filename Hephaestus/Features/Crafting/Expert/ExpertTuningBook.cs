using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Expert;

/// <summary>
/// The knobs for each expert recipe AS THIS CRAFTER MEETS IT. The best setting depends on the recipe
/// and on the stats (measured: the defaults deliver 10% of one Cosmic recipe, its own tuning 38%),
/// so the first time a recipe is seen it is tuned on simulated crafts in the background -
/// <see cref="ExpertTuner"/> on a few threads - and the craft in hand carries on meanwhile with the
/// nearest thing known: a preset for the recipe's shape, else the defaults.
/// In TWO STAGES: a quick one (a second or two) so that the first craft already plays something fitted,
/// then a deep one on four times the crafts, which is what finds the knobs a quick look cannot tell
/// from luck (measured 2026-11-12 on the Crumbling Aqueduct Fragment: the quick stage alone makes 33%
/// of the items, a deep tuning 59%). The answer changes as each stage lands.
/// Results are kept for the session, per recipe and stats.
///
/// Thread-safe: asked on the framework thread, answered to by the tuning task.
/// </summary>
public sealed class ExpertTuningBook
{
    /// <summary>Simulated crafts per setting tried in the quick stage. Small on purpose: this runs inside the game's process.</summary>
    public const int Crafts = 400;

    /// <summary>...and in the deep stage that follows it, on other seeds. 0 crafts = no deep stage.</summary>
    public const int DeepCrafts = 1600;

    /// <summary>Rounds of the deep stage, at most: it stops at the first round that changes nothing.</summary>
    public const int DeepRounds = 4;

    /// <summary>What a second is worth on a scored collectable, in points: what the tuned policy makes in one (measured, 9-13).</summary>
    public const double TimeValue = 10;

    /// <summary>
    /// What a second is worth on a Cosmic rated item the game does not label Expert (an EX mission's two or three
    /// items, not a Master mission's as-many-as-fit): enough that the solver does not dawdle, far too little to
    /// gamble the item for. Measured 2026-11-15 on the Voyager Relic Restoration Component and the Ruins Aetheric
    /// Gauge (CosmicCollectableBenchmark): at <see cref="TimeValue"/> one Component in six was left unfinished.
    /// </summary>
    public const double RatedTimeValue = 2;

    /// <summary>
    /// What a second is worth when an ordinary item is made in BULK (a delivered item is worth 1,000 points): the
    /// price the sandbox's five-recipe table was measured at. A one-off is made at 0 - quality first.
    /// </summary>
    public const double BulkTimeValue = 8;

    private sealed class Entry
    {
        public volatile ExpertTuning Tuning = ExpertTuning.Default;
        public int Started;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Func<Action, Task> _run;
    private readonly int _deepCrafts;
    private volatile string _status = "Idle — no expert recipe met yet.";

    /// <param name="run">Runs a tuning off the framework thread. Tests pass a synchronous runner.</param>
    /// <param name="deepCrafts">Crafts per setting tried in the deep stage; 0 = the quick stage only.</param>
    public ExpertTuningBook(Func<Action, Task>? run = null, int deepCrafts = DeepCrafts)
    {
        _run = run ?? (work => Task.Run(work));
        _deepCrafts = deepCrafts;
    }

    /// <summary>What the book last did, or why it is idle.</summary>
    public string Status => _status;

    /// <summary>
    /// The knobs for this problem, now and later: the answer changes once its tuning finishes.
    /// </summary>
    /// <param name="tune">False = presets and defaults only.</param>
    /// <param name="threads">Threads the tuning may use.</param>
    public Func<ExpertTuning> For(ExpertProblem problem, bool tune, int threads)
    {
        var entry = _entries.GetOrAdd(Key(problem), _ => new Entry { Tuning = Preset(problem) });
        if (tune && Interlocked.Exchange(ref entry.Started, 1) == 0)
        {
            _status = $"Tuning {problem.Name} on simulated crafts";
            try
            {
                _run(() => Tune(problem, entry, Math.Max(1, threads)));
            }
            catch (Exception ex)
            {
                _status = $"{problem.Name}: tuning could not start ({ex.GetType().Name}) — playing the preset";
            }
        }
        else if (!tune && entry.Started == 0)
        {
            _status = $"{problem.Name}: {entry.Tuning.Changes()} (tuning is switched off)";
        }

        return () => entry.Tuning;
    }

    private void Tune(ExpertProblem problem, Entry entry, int threads)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var problems = new[] { problem };
            var timeValue = problem.TimeValue;
            var uses = problem.Duty is null ? 0 : 1; // a craft is lent one use; the mission's stock is not this craft's to plan
            var start = entry.Tuning;
            var tuned = ExpertTuner.Tune(
                tuning => ExpertTuner.Scores(problems, tuning, firstSeed: 1, Crafts, timeValue, uses, threads),
                start, _ => { }, _ => string.Empty, maxRounds: 2, skip: knob => knob == Knob.DutyUses);

            entry.Tuning = tuned;
            _status = string.Create(CultureInfo.InvariantCulture,
                $"{problem.Name}: tuned in {clock.Elapsed.TotalSeconds:F1} s{(_deepCrafts > 0 ? ", tuning deeper" : "")} — {tuned.Changes()}");
            if (_deepCrafts <= 0)
                return;

            // The deep stage: from where the quick one got to, on crafts neither stage has seen before.
            tuned = ExpertTuner.Tune(
                tuning => ExpertTuner.Scores(problems, tuning, firstSeed: 100_001, _deepCrafts, timeValue, uses, threads),
                tuned, _ => { }, _ => string.Empty, maxRounds: DeepRounds, skip: knob => knob == Knob.DutyUses);

            entry.Tuning = tuned;
            _status = string.Create(CultureInfo.InvariantCulture, $"{problem.Name}: tuned in {clock.Elapsed.TotalSeconds:F1} s, both stages — {tuned.Changes()}");
        }
        catch (Exception ex)
        {
            _status = $"{problem.Name}: tuning failed ({ex.GetType().Name}) — playing the preset";
        }
    }

    /// <summary>
    /// Where the tuning starts from, and what is played until it is done. The three shapes are Cosmic
    /// Exploration's "Master" missions (the same three recipes for all eight trades), tuned in the
    /// tests' sandbox (MasterMissionBenchmark, last 2026-11-12, 5911 / 5503 / 641).
    /// </summary>
    public static ExpertTuning Preset(ExpertProblem problem)
    {
        var p = problem.Parameters;
        return (p.MaxProgress, p.MaxQuality, p.MaxDurability) switch
        {
            (7800, 25100, 60) => ExpertTuning.Parse(
                "VenerationAt=2, DurabilityRate=24, StepCost=16, IqValue=10, ManipulationBelow=0, PliantManipulationAt=0, PrimedManipulation=0, TricksBelowCp=60, TrainedPerfection=1, GoodInProgress=2, RateSlope=999, ByregotDurability=10, TricksWeight=0, Miracle=2, Strides=10, NetOn=3, Mend=1, PliantMend=1, Prep=3"),
            (6900, 22100, 45) => ExpertTuning.Parse(
                "DurabilityRate=24, IqValue=90, ComboCredit=0, PliantManipulationAt=0, TricksBelowCp=0, GreatStridesMinIq=4, TrainedPerfection=1, GoodInProgress=2, ByregotDurability=10, TricksWeight=20, Aim=90, WaitForGood=1, OmenStrides=2, NetOn=2, Perfection=1, ObserveCombo=1"),
            (7400, 26300, 80) => ExpertTuning.Parse(
                "VenerationAt=4, ManipulationBelow=0, PrimedManipulation=50, ImmaculateMissing=45, TricksBelowCp=0, GreatStridesMinIq=4, ObserveForDurability=0, GoodInProgress=2, RateSlope=8, ByregotDurability=10, Reach=30, WaitForGood=1, OmenStrides=2, NetOn=1, Mend=1, PliantMend=1"),
            _ => ExpertTuning.Default,
        };
    }

    /// <summary>Everything the best knobs can depend on: the recipe as the simulator sees it, the crafter, the goal.</summary>
    public static string Key(ExpertProblem problem)
    {
        var p = problem.Parameters;
        var tiers = problem.Tiers is null ? "-" : string.Join("/", problem.Tiers);
        return string.Create(CultureInfo.InvariantCulture,
            $"{p.MaxProgress}|{p.MaxQuality}|{p.MaxDurability}|{p.MaxCp}|{p.BaseProgress}|{p.BaseQuality}|{p.JobLevel}|{p.AllowedActions:X}|{(p.SplendorousTools ? 1 : 0)}|{problem.ConditionsFlag}|{problem.InitialQuality}|{problem.Target}|{tiers}|{problem.Duty}|{problem.TimeValue}|{(problem.ByHqChance ? 1 : 0)}");
    }
}
