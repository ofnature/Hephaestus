using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Planner;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Features.Crafting.Driving;

/// <summary>
/// The INNER loop: from the moment a Synthesis window opens to the moment it closes, decide the
/// next action. No Dalamud types and no game calls — it is ticked with a <see cref="SynthesisSnapshot"/>
/// and answers with the action to press (Auto) or just publishes its <see cref="Current"/>
/// recommendation (Assisted). All game access stays in the adapters that feed it.
///
/// Order of events for one craft:
///   1. Window opens -> ask the planner for a rotation (cached on disk after the first time) and wait.
///      An EXPERT recipe skips this: no fixed plan survives its conditions, so it gets the expert
///      solver (<see cref="ExpertAdvisor"/>) instead of a rotation and the live evaluator.
///      An ordinary recipe whose rotation does not reach its target on paper goes to the expert
///      solver as well, when the setup allows it (<see cref="CraftSetup.Tight"/>).
///   2. Each time the state settles -> evaluate it on a background thread.
///   3. Auto: press the recommendation, paced, until the state changes. Assisted: show it.
///   4. Every action the game accepts (ours or the player's) is reported through
///      <see cref="OnActionUsed"/> and replayed into the evaluator before its next evaluation.
///
/// Threading: <see cref="Tick"/> and <see cref="OnActionUsed"/> run on the framework thread. The
/// evaluator is only ever touched inside the single in-flight background evaluation, and observed
/// actions are handed to it THERE — so it is never used by two threads at once.
/// </summary>
public sealed class CraftSession
{
    /// <summary>
    /// The game updates progress, CP and buffs a few frames apart after an action. Evaluate only
    /// once the picture has stopped changing, or the decision is made on a half-updated state.
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(250);

    /// <summary>Gap between presses of the same recommendation while the state has not changed.</summary>
    public static readonly TimeSpan PressPacing = TimeSpan.FromMilliseconds(1200);

    /// <summary>Presses of one recommendation with nothing happening before that action is masked and we think again.</summary>
    public const int MaxPresses = 3;

    /// <summary>How long a craft waits for Raphael before carrying on with no baseline.</summary>
    public static readonly TimeSpan SolveTimeout = TimeSpan.FromSeconds(90);

    private readonly Func<SynthesisSnapshot, CraftSetup?> _setup;
    private readonly Func<BaselineRequest, Task<IReadOnlyList<CraftAction>?>>? _solve;
    private readonly Func<EvaluatorSettings> _settings;
    private readonly Func<Func<Recommendation>, Task<Recommendation>> _run;
    private readonly Func<ExpertProblem, ICraftAdvisor>? _expert;

    private bool _active;
    private CraftSetup _craft;
    private ICraftAdvisor? _evaluator;
    private bool _expertSeated;
    private float _miracleSeconds;
    private Task<IReadOnlyList<CraftAction>?>? _solveTask;
    private DateTime _solveDeadline;

    private readonly List<CraftAction> _unobserved = new(); // used, not yet shown to the evaluator
    private Combo _combo;
    private bool _stepTaken;
    private bool _trainedPerfectionUsed;
    private bool _heartAndSoulUsed;
    private bool _quickInnovationUsed;
    private int _lastStepIndex;

    private SynthesisSnapshot _settling;
    private DateTime _settlingSince;
    private Task<Recommendation>? _evaluation;
    private SynthesisSnapshot _evaluating;
    private SynthesisSnapshot _recommendedFor;
    private bool _hasRecommendation;
    private ulong _excluded;
    private int _presses;
    private DateTime _nextPressAt;

    /// <param name="setup">Builds the craft's fixed parameters from its first snapshot; null while the game data is not ready.</param>
    /// <param name="solve">Asks the planner (ours, or raphael-cli) for a rotation; null when there is none to ask. A null result means "no rotation".</param>
    /// <param name="run">Runs an evaluation off the framework thread. Tests pass a synchronous runner.</param>
    /// <param name="expert">Seats the expert solver at a craft whose setup names an expert problem; null = such crafts get the live evaluator too.</param>
    public CraftSession(
        Func<SynthesisSnapshot, CraftSetup?> setup,
        Func<BaselineRequest, Task<IReadOnlyList<CraftAction>?>>? solve,
        Func<EvaluatorSettings> settings,
        Func<Func<Recommendation>, Task<Recommendation>>? run = null,
        Func<ExpertProblem, ICraftAdvisor>? expert = null)
    {
        _setup = setup;
        _solve = solve;
        _settings = settings;
        _run = run ?? (work => Task.Run(work));
        _expert = expert;
    }

    /// <summary>The craft in hand is being made by the expert solver.</summary>
    public bool Expert => _active && _expertSeated;

    /// <summary>A synthesis is open and being followed.</summary>
    public bool Active => _active;

    /// <summary>The recommendation for the state on screen, if there is one yet.</summary>
    public Recommendation? Current { get; private set; }

    /// <summary>What the session is doing, or why it is not doing anything.</summary>
    public string Status { get; private set; } = "Idle — no synthesis in progress.";

    /// <summary>
    /// The synthesis last seen made a RATED collectable (a Cosmic mission's item): true = its quality stood at the top
    /// rating or above when last seen, false = below it; null = it was not such an item. Such items are never "high
    /// quality" in the game's sense - this is what the crafting window counts for them instead. Kept after the
    /// synthesis ends: the craft run reports the craft as made only once the window has closed.
    /// </summary>
    public bool? LastTopRating { get; private set; }

    /// <summary>A synthesis is over (finished, failed or quit): how it went, and how long it took.</summary>
    public event Action<SynthesisRecord>? SynthesisEnded;

    private DateTime _startedAt;
    private SynthesisSnapshot _last;
    private double _firstEstimate = -1;

    /// <param name="miracleSeconds">Cosmic Exploration: seconds left on Material Miracle, 0 when it is not running.</param>
    /// <returns>The action to press right now, or null. Only ever non-null when <paramref name="auto"/> is set.</returns>
    public CraftAction? Tick(SynthesisSnapshot? snapshot, bool transitioning, bool auto, DateTime now, float miracleSeconds = 0)
    {
        _miracleSeconds = miracleSeconds;

        if (snapshot is not { } current)
        {
            if (_active)
                End(now);
            return null;
        }

        // A lower step index than last seen means a new synthesis started without us seeing the gap.
        if (_active && current.StepIndex < _lastStepIndex)
            End(now);
        if (!_active && !Begin(current, now))
            return null;

        _lastStepIndex = current.StepIndex;
        _last = current;
        LastTopRating = _craft.Expert?.Tiers is { Length: > 0 } ratings ? current.Quality >= ratings[^1] : null;

        if (_evaluator == null && !TryCreateEvaluator(now))
            return null;

        if (transitioning)
        {
            Status = "Waiting for the action to resolve";
            return null;
        }

        // Let the picture settle before trusting it.
        if (current != _settling)
        {
            _settling = current;
            _settlingSince = now;
        }

        if (now - _settlingSince < SettleTime)
            return null;

        CollectEvaluation(current);

        if (!_hasRecommendation || _recommendedFor != current)
        {
            if (_hasRecommendation)
            {
                // The state moved on: whatever we were pressing landed (or the player acted).
                _hasRecommendation = false;
                Current = null;
                _excluded = 0;
                _presses = 0;
            }

            StartEvaluation(current);
            return null;
        }

        if (Current is not { Action: { } action } recommendation)
        {
            Status = "Nothing usable — the synthesis is over.";
            return null;
        }

        Status = recommendation.Reason;
        if (!auto || now < _nextPressAt)
            return null;

        if (_presses >= MaxPresses)
        {
            // The game will not take it, whatever the simulator thinks. Rule it out and think again.
            _excluded = ActionMask.With(_excluded, action);
            _hasRecommendation = false;
            Current = null;
            _presses = 0;
            Status = $"{action} is being refused by the game — choosing something else";
            return null;
        }

        _presses++;
        _nextPressAt = now + PressPacing;
        return action;
    }

    /// <summary>The game accepted a crafting action — pressed by us or by the player.</summary>
    public void OnActionUsed(CraftAction action)
    {
        if (!_active)
            return;

        _unobserved.Add(action);

        // The same bookkeeping the simulator does, for the parts of the state the game does not show.
        if (CraftSimulator.SpendsStep(action))
        {
            _combo = action switch
            {
                CraftAction.BasicTouch => Combo.BasicTouch,
                CraftAction.StandardTouch when _combo == Combo.BasicTouch => Combo.StandardTouch,
                CraftAction.Observe => Combo.StandardTouch,
                _ => Combo.None,
            };
            _stepTaken = true;
        }
        else if (_stepTaken)
        {
            _combo = Combo.None;
        }

        switch (action)
        {
            case CraftAction.TrainedPerfection:
                _trainedPerfectionUsed = true;
                break;
            case CraftAction.HeartAndSoul:
                _heartAndSoulUsed = true;
                break;
            case CraftAction.QuickInnovation:
                _quickInnovationUsed = true;
                break;
        }
    }

    /// <summary>The simulator's view of a snapshot: what the game shows plus what this session remembered.</summary>
    public CraftState ToState(in SynthesisSnapshot snapshot)
    {
        var parameters = _craft.Parameters;
        return new CraftState
        {
            Cp = snapshot.Cp,
            Durability = snapshot.Durability,
            Progress = snapshot.Progress,
            Quality = snapshot.Quality,
            InnerQuiet = (byte)Math.Clamp(snapshot.InnerQuiet, 0, 10),
            WasteNot = (byte)Math.Clamp(snapshot.WasteNot, 0, 255),
            Innovation = (byte)Math.Clamp(snapshot.Innovation, 0, 255),
            Veneration = (byte)Math.Clamp(snapshot.Veneration, 0, 255),
            GreatStrides = (byte)Math.Clamp(snapshot.GreatStrides, 0, 255),
            MuscleMemory = (byte)Math.Clamp(snapshot.MuscleMemory, 0, 255),
            Manipulation = (byte)Math.Clamp(snapshot.Manipulation, 0, 255),
            TrainedPerfectionActive = snapshot.TrainedPerfectionActive,
            HeartAndSoulActive = snapshot.HeartAndSoulActive,
            Expedience = snapshot.Expedience,
            TrainedPerfectionAvailable = !_trainedPerfectionUsed && Unlocked(parameters, CraftAction.TrainedPerfection),
            HeartAndSoulAvailable = !_heartAndSoulUsed && Unlocked(parameters, CraftAction.HeartAndSoul),
            QuickInnovationAvailable = !_quickInnovationUsed && Unlocked(parameters, CraftAction.QuickInnovation),
            Combo = _stepTaken ? _combo : Combo.SynthesisBegin,

            // Cosmic Exploration: the mission's duty action, as the game shows it.
            SteadyHand = (byte)Math.Clamp(snapshot.SteadyHand, 0, 255),
            SteadyHandCharges = (byte)(snapshot.DutyAction == CraftAction.StellarSteadyHand ? Math.Clamp(snapshot.DutyCharges, 0, 255) : 0),
            MaterialMiracleCharges = (byte)(snapshot.DutyAction == CraftAction.MaterialMiracle ? Math.Clamp(snapshot.DutyCharges, 0, 255) : 0),
            MaterialMiracle = (byte)(snapshot.MaterialMiracleActive
                ? Math.Clamp((int)(_miracleSeconds * 4), 1, CraftSimulator.MaterialMiracleQuarterSeconds)
                : 0),
        };
    }

    private static bool Unlocked(in CraftParameters parameters, CraftAction action)
        => parameters.JobLevel >= CraftSimulator.Level(action) && ActionMask.Has(parameters.AllowedActions, action);

    private bool Begin(in SynthesisSnapshot first, DateTime now)
    {
        if (_setup(first) is not { } craft)
        {
            Status = "Waiting for the recipe data";
            return false;
        }

        _active = true;
        _craft = craft;
        _startedAt = now;
        _last = first;
        _firstEstimate = -1;
        _evaluator = null;
        _expertSeated = false;
        _evaluation = null;
        _hasRecommendation = false;
        Current = null;
        _unobserved.Clear();
        _combo = Combo.None;
        // Joining a craft that is already under way (plugin reload): the first-step actions are gone.
        _stepTaken = first.StepIndex > 1;
        _trainedPerfectionUsed = false;
        _heartAndSoulUsed = false;
        _quickInnovationUsed = false;
        _excluded = 0;
        _presses = 0;
        _settling = default;
        _lastStepIndex = first.StepIndex;

        // An expert recipe is not made from a plan: no solve to wait for.
        if (craft.Expert != null && _expert != null)
        {
            _solveTask = null;
            Status = "Expert recipe — the expert solver takes it";
            return true;
        }

        // Raphael solves from the START of a craft, so a baseline is only meaningful at step 1.
        _solveTask = _solve != null && first.StepIndex <= 1 ? _solve(craft.Request) : null;
        _solveDeadline = now + SolveTimeout;
        Status = _solveTask != null ? "Planning the rotation" : "Starting without a rotation";
        return true;
    }

    private bool TryCreateEvaluator(DateTime now)
    {
        if (_craft.Expert is { } problem && _expert != null)
        {
            _evaluator = _expert(problem);
            _expertSeated = true;
            return true;
        }

        IReadOnlyList<CraftAction> baseline = Array.Empty<CraftAction>();
        if (_solveTask != null)
        {
            if (!_solveTask.IsCompleted)
            {
                if (now < _solveDeadline)
                {
                    Status = "Planning the rotation";
                    return false;
                }

                Status = "The planner is taking too long — carrying on without a rotation";
            }
            else if (_solveTask.IsCompletedSuccessfully && _solveTask.Result is { } solved)
            {
                baseline = solved;
            }
        }

        _solveTask = null;

        // A rotation that does not carry the item to its target on paper is a plan to fall short: where the setup
        // allows it, the solver that works WITH the conditions makes the item instead. (Also when there is no
        // rotation at all - none was found, the planner ran out of time, or the craft was joined part-way.)
        if (_craft.Tight is { } tight && _expert != null
            && !OwnPlan.Carries(_craft.Parameters, baseline, _craft.Request.InitialQuality, tight.Target, tight.ByHqChance))
        {
            _evaluator = _expert(tight);
            _expertSeated = true;
            Status = "No rotation reaches full quality on paper — the expert solver takes it";
            return true;
        }

        _evaluator = new LiveEvaluator(_craft.Parameters, baseline, _craft.ConditionsFlag, _settings());
        return true;
    }

    private void StartEvaluation(in SynthesisSnapshot snapshot)
    {
        if (_evaluation != null || _evaluator == null)
            return;

        var evaluator = _evaluator;
        var state = ToState(snapshot);
        var condition = snapshot.Condition;
        var excluded = _excluded;
        var observed = _unobserved.ToArray();
        _unobserved.Clear();

        _evaluating = snapshot;
        Status = "Thinking";
        _evaluation = _run(() =>
        {
            foreach (var action in observed)
                evaluator.Observe(action);
            return evaluator.Recommend(state, condition, excluded);
        });
    }

    private void CollectEvaluation(in SynthesisSnapshot current)
    {
        if (_evaluation is not { IsCompleted: true } finished)
            return;

        _evaluation = null;
        if (!finished.IsCompletedSuccessfully)
        {
            Status = "Evaluation failed — retrying";
            return;
        }

        // An answer for a state that is no longer on screen is thrown away.
        if (_evaluating != current)
            return;

        Current = finished.Result;
        if (_firstEstimate < 0)
            _firstEstimate = finished.Result.SecondsLeft;
        _recommendedFor = current;
        _hasRecommendation = true;
        _presses = 0;
        _nextPressAt = default;
    }

    private void End(DateTime now)
    {
        var record = new SynthesisRecord(
            _last.RecipeId, _startedAt, Math.Max(0, (now - _startedAt).TotalSeconds), _last.StepIndex, _last.Progress, _last.MaxProgress,
            _last.Quality, _last.MaxQuality, _expertSeated, LastTopRating, _firstEstimate);

        _active = false;
        _expertSeated = false;
        _evaluator = null;
        _evaluation = null; // a still-running evaluation finishes against its own captured state and is dropped
        _solveTask = null;
        _hasRecommendation = false;
        Current = null;
        _unobserved.Clear();
        _lastStepIndex = 0;
        Status = "Idle — no synthesis in progress.";
        SynthesisEnded?.Invoke(record);
    }
}
