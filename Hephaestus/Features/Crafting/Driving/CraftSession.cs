using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
///   1. Window opens -> ask Raphael for a baseline (cached on disk after the first time) and wait.
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

    private bool _active;
    private CraftSetup _craft;
    private LiveEvaluator? _evaluator;
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
    /// <param name="solve">Asks Raphael for a baseline; null when the solver is not available. A null result means "no baseline".</param>
    /// <param name="run">Runs an evaluation off the framework thread. Tests pass a synchronous runner.</param>
    public CraftSession(
        Func<SynthesisSnapshot, CraftSetup?> setup,
        Func<BaselineRequest, Task<IReadOnlyList<CraftAction>?>>? solve,
        Func<EvaluatorSettings> settings,
        Func<Func<Recommendation>, Task<Recommendation>>? run = null)
    {
        _setup = setup;
        _solve = solve;
        _settings = settings;
        _run = run ?? (work => Task.Run(work));
    }

    /// <summary>A synthesis is open and being followed.</summary>
    public bool Active => _active;

    /// <summary>The recommendation for the state on screen, if there is one yet.</summary>
    public Recommendation? Current { get; private set; }

    /// <summary>What the session is doing, or why it is not doing anything.</summary>
    public string Status { get; private set; } = "Idle — no synthesis in progress.";

    /// <returns>The action to press right now, or null. Only ever non-null when <paramref name="auto"/> is set.</returns>
    public CraftAction? Tick(SynthesisSnapshot? snapshot, bool transitioning, bool auto, DateTime now)
    {
        if (snapshot is not { } current)
        {
            if (_active)
                End();
            return null;
        }

        // A lower step index than last seen means a new synthesis started without us seeing the gap.
        if (!_active || current.StepIndex < _lastStepIndex)
        {
            if (!Begin(current, now))
                return null;
        }

        _lastStepIndex = current.StepIndex;

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
        _evaluator = null;
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

        // Raphael solves from the START of a craft, so a baseline is only meaningful at step 1.
        _solveTask = _solve != null && first.StepIndex <= 1 ? _solve(craft.Request) : null;
        _solveDeadline = now + SolveTimeout;
        Status = _solveTask != null ? "Solving the baseline with Raphael" : "Starting without a baseline";
        return true;
    }

    private bool TryCreateEvaluator(DateTime now)
    {
        IReadOnlyList<CraftAction> baseline = Array.Empty<CraftAction>();
        if (_solveTask != null)
        {
            if (!_solveTask.IsCompleted)
            {
                if (now < _solveDeadline)
                {
                    Status = "Solving the baseline with Raphael";
                    return false;
                }

                Status = "Raphael is taking too long — carrying on without a baseline";
            }
            else if (_solveTask.IsCompletedSuccessfully && _solveTask.Result is { } solved)
            {
                baseline = solved;
            }
        }

        _solveTask = null;
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
        _recommendedFor = current;
        _hasRecommendation = true;
        _presses = 0;
        _nextPressAt = default;
    }

    private void End()
    {
        _active = false;
        _evaluator = null;
        _evaluation = null; // a still-running evaluation finishes against its own captured state and is dropped
        _solveTask = null;
        _hasRecommendation = false;
        Current = null;
        _unobserved.Clear();
        _lastStepIndex = 0;
        Status = "Idle — no synthesis in progress.";
    }
}
