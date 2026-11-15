using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Crafting;
using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Solver;
using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;
using Hephaestus.Features.Workshop;
using Hephaestus.Ipc;
using Hephaestus.Services;
using Hephaestus.Services.Game;
using Hephaestus.Windows;

namespace Hephaestus;

/// <summary>
/// Composition root. Feature logic lives in Features/ (pure, unit-tested, no Dalamud types); game
/// interop lives in Services/Game/ (thin fail-open adapters) — the same split as Charon.
/// </summary>
public sealed class HephaestusPlugin : IDalamudPlugin
{
    public const string PluginVersion = "0.1.0";

    private const string CommandName = "/hephaestus";
    private const string CommandAlias = "/heph";

    /// <summary>Gil is item 1 as far as the inventory is concerned.</summary>
    private const uint GilItemId = 1;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IFramework _framework;
    private readonly IGameInventory _inventory;
    private readonly INotificationManager _notifications;
    private readonly IPluginLog _log;
    private readonly IChatGui _chat;
    private readonly string _craftTimesDirectory;

    private readonly HephaestusConfig _config;
    private readonly WindowSystem _windowSystem = new("Hephaestus");
    private readonly MainWindow _mainWindow;
    private readonly CraftingLogWindow _craftingLogWindow;
    private readonly CraftAllOverlay _craftAllOverlay;
    private readonly AssistOverlay _assistOverlay;
    private readonly HephaestusIpc _ipc;
    private readonly QueueContextMenu _contextMenu;
    private readonly CompanyCraftReader _companyCraftReader;
    private readonly WorkshopBuildStore _workshopStore;
    private readonly WorkshopBuildWindow _workshopWindow;

    // Buying (NPC vendors) and gathering (Demeter): the Buy/Gather List tabs, and the queue's own fetches.
    private readonly SupplyDesk _desk;

    // What this character's retainers hold, learned from any retainer that gets opened.
    private readonly CharacterRetainers _retainers;
    private readonly RetainerWatcher _retainerWatcher;

    private readonly AccountReader _account;

    // The queue: item after item, changing job in between. It sequences the craft runs below.
    private readonly CharacterQueue _characterQueue;
    private readonly CraftOutcomeStore _outcomes;
    private readonly QueueRun _queue;
    private readonly JobSwitchGame _jobs;
    private readonly RecipeInfoReader _recipes;
    private bool _oneOff; // the engine is working through an unsaved one-off list, not the player's saved queue...
    private bool _externalRequest; // ...and that one-off is another plugin's (CraftItem / CraftList), not Craft All on another job
    private bool _queueWasActive;

    // Hephaestus.RequestStopAfterRun: finish the synthesis in progress, start nothing more, leave the crafting stance.
    private readonly StopAfterRun _stopAfterRun = new();
    private const string StopAfterRunReason = "another plugin asked to stop after the craft in progress";

    // Outer loop: one recipe x N from the crafting log.
    private readonly CraftingLogGame _craftingLog;
    private readonly CraftRun _run = new();
    private CraftRunObservation _observation;

    // Inner loop: the actions of one synthesis.
    private readonly SynthesisReader _synthesis;
    private readonly CraftActionExecutor _executor;
    private readonly CraftActionHook _actionHook;
    private readonly CraftSession _session;
    private readonly CraftRunLauncher _launcher;
    private readonly JobChange _jobChange = new();
    private readonly GearFloor _gearFloor = new();
    private readonly ExpertTuningBook _expertTuning;
    private readonly PlannerRunner _planner;
    private readonly CraftSetupReader _setupReader;
    private readonly MaterialPlanner _materials;

    // Pre-solves the queue's baselines one job at a time, between syntheses.
    private readonly BaselineWarmup? _warmup;
    private uint _warmedJob;

    public HephaestusPlugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IClientState clientState,
        IGameInventory inventory,
        ICondition condition,
        IDataManager dataManager,
        IGameGui gameGui,
        IObjectTable objectTable,
        IGameInteropProvider gameInterop,
        ITextureProvider textureProvider,
        IContextMenu contextMenu,
        INotificationManager notifications,
        IPluginLog log,
        IChatGui chat)
    {
        _chat = chat;
        _craftTimesDirectory = System.IO.Path.Combine(pluginInterface.ConfigDirectory.FullName, "craft-times");
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _framework = framework;
        _inventory = inventory;
        _notifications = notifications;
        _log = log;

        _config = pluginInterface.GetPluginConfig() as HephaestusConfig ?? new HephaestusConfig();
        if (_config.Migrate())
            SaveConfig();

        _account = new AccountReader(condition);
        var configDirectory = pluginInterface.ConfigDirectory.FullName;
        _characterQueue = new CharacterQueue(new CraftListStore(configDirectory));
        _outcomes = new CraftOutcomeStore(configDirectory);
        _run.CraftCompleted += OnCraftCompleted;
        _retainers = new CharacterRetainers(new RetainerIndexStore(configDirectory));
        _retainerWatcher = new RetainerWatcher(_retainers, log);
        _craftingLog = new CraftingLogGame(gameGui, condition, dataManager);
        _jobs = new JobSwitchGame(objectTable, condition, dataManager, log);
        _recipes = new RecipeInfoReader(dataManager);
        _companyCraftReader = new CompanyCraftReader(dataManager);
        _workshopStore = new WorkshopBuildStore(configDirectory);
        _workshopWindow = new WorkshopBuildWindow(_companyCraftReader, _workshopStore, _account, QueueWorkshopPart);
        _windowSystem.AddWindow(_workshopWindow);

        // A sub-craft is only planned where it would work UNATTENDED: a saved gearset for the job, and
        // the level for the recipe. (An entry the player listed himself is taken at his word.)
        _materials = new MaterialPlanner(
            _recipes.Read, _recipes.RecipesFor,
            recipe => _jobs.HasGearsetFor(recipe.JobId) && _jobs.LevelOf(recipe.JobId) >= recipe.JobLevel,
            _craftingLog.HeldInBags, _recipes.ItemName);
        var vendors = new VendorCatalog(dataManager, log);
        var vendorLocations = new VendorLocations(dataManager, log);
        var travel = new TravelCatalog(dataManager, log);
        var nav = new NavClient(pluginInterface);
        var lifestream = new LifestreamClient(pluginInterface);
        var trip = new VendorTrip(new VendorWorld(
            clientState, gameGui, objectTable, condition, _craftingLog, vendors, vendorLocations, travel, nav, lifestream, log));
        var retainerWorld = new RetainerWorld(clientState, gameGui, objectTable, condition, _craftingLog, travel, nav, lifestream, log);
        var retainerTrip = new RetainerTrip(
            retainerWorld,
            holdersOf: HoldersOf,
            neverIndexed: UnknownRetainerIds,
            nameOf: id => retainerWorld.Roster.FirstOrDefault(r => r.Id == id).Name is { Length: > 0 } name ? name : $"retainer {id}",
            withdrew: (retainer, item, nq, hq) => _retainers.Index.Withdrew(retainer, item, nq, hq));
        var chestWorld = new ChestWorld(gameGui, objectTable, condition, commandManager, dataManager, _craftingLog, nav, lifestream, log);
        var chestTrip = new ChestTrip(chestWorld);
        _desk = new SupplyDesk(
            _materials, _characterQueue, () => _jobs.CurrentJob, vendors, () => _craftingLog.HeldInBags(GilItemId),
            new DemeterGatherClient(pluginInterface, log), trip, retainerTrip, _retainers,
            new CharonRetainerClient(pluginInterface, log), () => retainerWorld.Roster,
            chestTrip, new CharonChestClient(pluginInterface, log), chestWorld.HeldOnPage, chestWorld.PageLoaded,
            queueBusy: QueueOrRunActive,
            logOpen: () => _observation.LogOpen,
            closeLog: _craftingLog.CloseLog,
            recipeName: id => _recipes.Read(id)?.Name ?? $"recipe {id}",
            freeTrial: () => _account.IsFreeTrial);

        // The queue fetches on its own only where this character has automatic sourcing on (free
        // trials by default). Otherwise it plans from the bags alone and flags what is short.
        _queue = new QueueRun(
            _recipes.Read, _jobs.HasGearsetFor, _materials, () => _config.Queue.ExpandSubCrafts,
            () => _config.AutoSourcingFor(_account.LocalContentId, _account.IsFreeTrial) ? _desk.Sources() : null);

        // ---- the solver, wired to the game ----------------------------------------------------------
        var actions = new GameActionMap(dataManager);
        _synthesis = new SynthesisReader(gameGui, objectTable, condition);
        _executor = new CraftActionExecutor(objectTable, actions);
        _setupReader = new CraftSetupReader(dataManager, objectTable, actions, _config, BulkCrafting);

        // One cache for both planners (their keys differ): two instances would race on the same file.
        var baselineCache = new BaselineCache(configDirectory);
        int RaphaelThreads() => _config.Solver.RaphaelThreads > 0 ? _config.Solver.RaphaelThreads : Math.Max(1, Environment.ProcessorCount / 2);
        var raphael = new RaphaelRunner(
            pluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty,
            baselineCache,
            RaphaelThreads,
            log);
        if (!raphael.Available && _config.Solver.Planner == PlanSource.Raphael)
            log.Warning("Hephaestus: raphael-cli was not found next to the plugin — our own planner plans the rotations instead");

        // Rotations for ordinary recipes: our own planner unless Settings ask for Raphael (and it is there to ask).
        _planner = new PlannerRunner(baselineCache, RaphaelThreads, message => log.Info("Hephaestus planner: {0}", message));
        System.Threading.Tasks.Task<IReadOnlyList<Features.Crafting.Simulation.CraftAction>?> Plan(BaselineRequest request)
            => _config.Solver.Planner == PlanSource.Raphael && raphael.Available ? raphael.SolveAsync(request) : _planner.SolveAsync(request);

        // Expert recipes: the expert solver, tuned per recipe and stats on background threads the first time it is met.
        _expertTuning = new ExpertTuningBook();
        _session = new CraftSession(
            _setupReader.Read,
            Plan,
            () => new EvaluatorSettings
            {
                MaxDepth = _config.Solver.LookaheadMaxDepth,
                Budget = TimeSpan.FromMilliseconds(_config.Solver.LookaheadBudgetMs),
                DeviationPercent = _config.Solver.BaselineDeviationPercent,
            },
            expert: problem => new ExpertAdvisor(
                problem,
                _expertTuning.For(problem, _config.Solver.ExpertTuning, threads: Math.Max(1, Environment.ProcessorCount / 4)),
                _config.Solver.ExpertFutures > 0
                    ? new SearchSettings(
                        Samples: Math.Clamp(_config.Solver.ExpertFutures, 32, 512),
                        Margin: problem.TimeValue > 0 ? 0.015 : 0.03,
                        TimeValue: problem.TimeValue)
                    : null,
                TimeSpan.FromMilliseconds(_config.Solver.LookaheadBudgetMs),
                seed: (ulong)DateTime.UtcNow.Ticks));

        _actionHook = new CraftActionHook(gameInterop, actions, log);
        _actionHook.ActionUsed += _session.OnActionUsed;
        _session.SynthesisEnded += OnSynthesisEnded;

        // The live solve and the game come first: nothing is pre-solved while a synthesis is open.
        _warmup = new BaselineWarmup(Plan, paused: () => !_config.Solver.PreSolveQueue || _session.Active);

        // ---- windows --------------------------------------------------------------------------------
        _mainWindow = new MainWindow(
            _config, SaveConfig, _account, _characterQueue, _queue, StartQueue, StopEverything,
            () => _oneOff || (_run.Active && !_queue.Active) || SupplyBusy,
            _materials, () => _jobs.CurrentJob,
            _desk, vendorLocations,
            () => _desk.RetainerSourceStatus(_retainerWatcher.Status),
            dataManager,
            () => _session.Status,
            () => _config.Solver.Planner != PlanSource.Raphael ? "not used — our own planner plans the rotations (Settings > Solver)"
                : raphael.Available ? raphael.Status
                : "raphael-cli not found next to the plugin — our own planner is used instead",
            () => _warmup?.Status ?? "off — raphael-cli not found",
            () => _workshopWindow.IsOpen = true,
            () => _stopAfterRun.Status,
            () => !_config.Solver.ExpertSolver ? "off — switched off in Settings"
                : _session.Expert ? $"at this craft — {_expertTuning.Status}"
                : _expertTuning.Status,
            () => _config.Solver.Planner == PlanSource.Own ? _planner.Status : "not used — Raphael plans the rotations (Settings > Solver)");
        _windowSystem.AddWindow(_mainWindow);

        // Settings are read when a run starts or resumes, so a change in the window applies to the next run.
        var launcher = _launcher = new CraftRunLauncher(_run, RunSettings, blocked: () => _queue.Active || SupplyBusy, viaJobChange: CraftOnAnotherJob);

        _craftingLogWindow = new CraftingLogWindow(
            _config, SaveConfig, _account, _craftingLog, new ConsumableReader(dataManager), _run, launcher, AddToQueue, () => _observation,
            recipeId => _outcomes.Lookup(_account.LocalContentId, recipeId));
        _windowSystem.AddWindow(_craftingLogWindow);

        _craftAllOverlay = new CraftAllOverlay(_craftingLog, _run, launcher, AddToQueue, () => _observation);
        _windowSystem.AddWindow(_craftAllOverlay);

        _assistOverlay = new AssistOverlay(
            _session, _synthesis, actions, textureProvider,
            new HotbarReader(gameGui, objectTable, actions),
            // In Auto the plugin presses for you, so there is no button for you to find.
            () => _config.Solver.HighlightHotbar && _config.ModeFor(_account.LocalContentId) == ExecutionMode.Assisted);
        _windowSystem.AddWindow(_assistOverlay);

        _ipc = new HephaestusIpc(
            pluginInterface, () => Busy, CraftItemForAnotherPlugin, StopForAnotherPlugin, QueueStatusJson,
            CraftListForAnotherPlugin, AddToQueueForAnotherPlugin, StartQueueForAnotherPlugin, RequestStopAfterRun,
            CraftSelectedForAnotherPlugin, ChangeJobForAnotherPlugin, FinishUnderGearGateForAnotherPlugin);

        _contextMenu = new QueueContextMenu(
            contextMenu, gameGui, _recipes,
            () => _config.Queue.ContextMenu && _characterQueue.ContentId != 0,
            () => _jobs.CurrentJob, AddToQueue, log);

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Hephaestus window (short alias: /heph).",
        });
        _commandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Short alias for /hephaestus.",
        });

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;
        _pluginInterface.UiBuilder.OpenConfigUi += OpenMainWindow;
        _framework.Update += OnFrameworkUpdate;
        _inventory.InventoryChangedRaw += OnInventoryChanged;

        _log.Info("Hephaestus v{0} loaded", PluginVersion);
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _inventory.InventoryChangedRaw -= OnInventoryChanged;
        _desk.StopAll(); // leave nobody walking or teleporting on a plugin that is gone
        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        _pluginInterface.UiBuilder.OpenConfigUi -= OpenMainWindow;

        _commandManager.RemoveHandler(CommandName);
        _commandManager.RemoveHandler(CommandAlias);
        _windowSystem.RemoveAllWindows();

        _actionHook.ActionUsed -= _session.OnActionUsed;
        _session.SynthesisEnded -= OnSynthesisEnded;
        _actionHook.Dispose();
        _run.CraftCompleted -= OnCraftCompleted;
        _contextMenu.Dispose();
        _ipc.Dispose();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        try
        {
            _observation = _craftingLog.Observe(_run.RecipeId);
            TickStopAfterRun(now); // before the queue and the run, so neither starts anything once asked
            TickJobChange(now);
            TickQueue(now); // first, so a craft it starts begins this same frame
            _desk.Tick(now);
            TickRun(now);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hephaestus: queue / craft run update failed");
            _queue.Halt("internal error — see /xllog");
            _run.Halt(CraftRunHalt.InternalError, "internal error — see /xllog");
        }

        // A gear floor belongs to the work it was asked for: that work has stopped, however it did.
        if (_gearFloor.Percent > 0 && !_queue.Active && !_run.Active)
        {
            _gearFloor.Drop();
            _log.Information("Hephaestus: the gear floor is dropped — the work it was asked for has stopped");
        }

        try
        {
            TickSession(now);
            _warmup?.Tick(now);
            _retainerWatcher.Tick(now);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Hephaestus: synthesis update failed");
        }
    }

    /// <summary>The queue: pick the next item, get onto its job, hand it to a craft run, judge how that ended.</summary>
    private void TickQueue(DateTime now)
    {
        // The queue belongs to a character. If that changes under a running queue, stop — do not carry on as someone else.
        if (_characterQueue.Follow(_account.LocalContentId))
        {
            _desk.Forget(); // another character's buy and gather lists mean nothing here
            _retainers.Follow(_account.LocalContentId);
            _retainerWatcher.Forget();
            if (_queue.List != null)
            {
                StopEverything();
                _queue.Clear();
                _oneOff = false;
                _externalRequest = false;
                _queueWasActive = false;
            }
        }

        // The field catches a Halt that landed BETWEEN ticks (Stop from a window, or over IPC).
        var wasActive = _queue.Active || _queueWasActive;
        var command = _queue.Tick(
            new QueueObservation(
                _jobs.CurrentJob, _jobs.CanSwitch, _observation.LogOpen, _run.Active, _run.Completed, _run.HaltKind, _run.Status,
                _desk.QueueFetchActive, _desk.QueueFetchNeedsPerson, _desk.QueueFetchStatus, _desk.QueueFetchNotes, _run.HaltReason),
            now);

        switch (command.Kind)
        {
            case QueueCommandKind.CloseLog:
                _craftingLog.CloseLog();
                break;
            case QueueCommandKind.SwitchJob:
                _jobs.Equip(command.JobId); // accepted or not, the queue watches the job itself and retries or flags
                break;
            case QueueCommandKind.StartCraft:
                _run.Start(command.RecipeId, command.Count, RunSettings());
                break;
            case QueueCommandKind.ClearRun:
                _run.Clear();
                break;
            case QueueCommandKind.Fetch when command.Fetch is { } request:
                _desk.StartForQueue(request); // the queue judges it by the bags once it ends
                break;
        }

        WarmBaselines();

        if (wasActive && !_queue.Active)
            Announce(_oneOff ? OneOffOutcome() : _queue.Status, warning: _queue.CanResume || _queue.ItemsSkipped > 0);

        // A one-off is finished with, however it ended: another plugin saw IsBusy fall and judges the
        // outcome itself; a Craft All on another job leaves its run behind to Resume, like any other.
        // The player's own list takes the engine back.
        if (_oneOff && !_queue.Active)
        {
            _queue.Clear();
            _oneOff = false;
            _externalRequest = false;
        }

        _queueWasActive = _queue.Active;
    }

    /// <summary>
    /// Pre-solve the baselines for the job the queue is on: once per job it lands on (and per start),
    /// every runnable entry and planned sub-craft of that job. Only the current job's stats are
    /// readable, so other jobs wait until the queue gets there.
    /// </summary>
    private void WarmBaselines()
    {
        if (_warmup == null)
            return;

        if (!_queue.Active || _queue.List is not { } list)
        {
            _warmedJob = 0;
            if (_warmup.Pending > 0)
                _warmup.Clear(); // the queue stopped: solving for it would be wasted
            return;
        }

        var job = _jobs.CurrentJob;
        if (job == 0 || job == _warmedJob || _queue.Phase == QueuePhase.SwitchingJob)
            return;

        _warmedJob = job;
        var requests = new List<(BaselineRequest, string)>();
        foreach (var item in list.Items)
        {
            if (item.IsRunnable && _recipes.Read(item.RecipeId) is { } info && info.JobId == job
                && _setupReader.RequestFor(item.RecipeId) is { } request)
                requests.Add((request, info.Name));
        }

        if (_config.Queue.ExpandSubCrafts)
        {
            foreach (var subCraft in _materials.Plan(list.Items, job).SubCrafts)
            {
                if (subCraft.Recipe.JobId == job && _setupReader.RequestFor(subCraft.Recipe.RecipeId) is { } request)
                    requests.Add((request, subCraft.Recipe.Name));
            }
        }

        var added = _warmup.Enqueue(requests);
        if (added > 0)
            _log.Debug("Hephaestus: pre-solving {0} baseline(s) for job {1}", added, job);
    }

    /// <summary>A one-off list is forgotten the moment it ends, so what it flagged is said now or never.</summary>
    private string OneOffOutcome()
    {
        var flagged = _queue.List?.Items.Where(i => i.SkipReason != null)
            .Select(i => $"{_recipes.Read(i.RecipeId)?.Name ?? $"recipe {i.RecipeId}"} ({i.Crafted} / {i.Quantity} made): {i.SkipReason}")
            .ToArray() ?? Array.Empty<string>();
        return flagged.Length == 0 ? _queue.Status : $"{_queue.Status} {string.Join("; ", flagged)}";
    }

    /// <summary>Outer loop: let the run decide from the crafting log, execute the one command it answers with.</summary>
    private void TickRun(DateTime now)
    {
        var wasActive = _run.Active;
        var command = _run.Tick(_observation, now);
        _craftingLog.Execute(command, _run.RecipeId);

        // A run ending is the one thing an unattended box has to say out loud — Stop included,
        // since the same line confirms the click landed.
        // ...unless the queue is sequencing runs: then IT speaks, once, when the whole thing ends.
        if (wasActive && !_run.Active && !_queue.Active)
            Announce(_run.Status, warning: _run.CanResume);

        // The crafting-log window rides the log, and stays up through a run (the log itself is
        // hidden during a synthesis) so progress and Stop are always reachable.
        _craftingLogWindow.IsOpen = _observation.LogOpen || _run.Active;

        // The Craft All control lives inside the log, so it exists exactly while the log does.
        _craftAllOverlay.IsOpen = _observation.LogOpen;
    }

    /// <summary>
    /// Is the craft about to start one of many? Time decides in bulk crafting; a one-off is made quality first
    /// (user, 2026-11-11). Asked once per craft, when its setup is read.
    /// </summary>
    private bool BulkCrafting() => _config.Solver.Intent switch
    {
        CraftIntent.Bulk => true,
        CraftIntent.QualityFirst => false,
        _ => _run.Active && _run.Requested - _run.Completed >= Math.Max(2, _config.Solver.BulkFrom),
    };

    /// <summary>Inner loop: read the synthesis, let the session decide, press what it answers with (Auto only).</summary>
    private void TickSession(DateTime now)
    {
        var snapshot = _config.Solver.Enabled ? _synthesis.Read() : null;

        // UseAction is also the combat hot path: our hook exists in it only while a synthesis is open.
        _actionHook.SetEnabled(snapshot != null);

        var auto = _config.ModeFor(_account.LocalContentId) == ExecutionMode.Auto;
        if (_session.Tick(snapshot, _synthesis.Transitioning, auto, now, _synthesis.MaterialMiracleSeconds) is { } action && !_executor.Use(action))
            _log.Debug("Hephaestus: the game refused {0}", action);

        _assistOverlay.IsOpen = _session.Active;
    }

    private void Announce(string status, bool warning)
    {
        _log.Info("Hephaestus: {0}", status);
        _notifications.AddNotification(new Notification
        {
            Title = "Hephaestus",
            Content = status,
            Type = warning ? NotificationType.Warning : NotificationType.Success,
        });
    }

    private CraftRunSettings RunSettings()
    {
        var gate = _config.Queue.GearConditionThresholdPercent;
        var floor = _gearFloor.Gate(gate); // lower than the gate only while a caller asked (FinishUnderGearGate)
        return new(_config.Queue.MaxCraftRetries, floor, GearFloor: floor < gate);
    }

    /// <summary>
    /// Hephaestus.FinishUnderGearGate: the work in hand - going, or halted on the gear gate - may go on down to a
    /// floor under the player's gate. A run halted on the gate is resumed; the floor is dropped by the frame tick
    /// the moment that work has stopped.
    /// </summary>
    private bool FinishUnderGearGateForAnotherPlugin(int floorPercent)
    {
        var going = _queue.Active || _run.Active;
        var halted = !Busy && _run.CanResume && _run.HaltKind == CraftRunHalt.GearGate;
        if (!_gearFloor.Request(floorPercent, _config.Queue.GearConditionThresholdPercent, _observation.MinGearConditionPercent, going || halted))
            return false;

        if (_run.Active)
            _run.Adopt(RunSettings());
        else if (halted)
            _run.Resume(RunSettings());

        _log.Information(
            "Hephaestus: another plugin asked for the work in hand to go on under the gear gate, down to {0}% (asked {1}%, gate {2}%){3}",
            _gearFloor.Percent, floorPercent, _config.Queue.GearConditionThresholdPercent, halted ? " — the halted run is resumed" : string.Empty);
        return true;
    }

    /// <summary>Start, or carry on with, the logged-in character's own queue.</summary>
    private void StartQueue()
    {
        if (_queue.Active || _run.Active || _oneOff || SupplyBusy || _characterQueue.ContentId == 0)
            return;

        _run.Clear(); // a halted Craft All run must not linger as "resumable" behind the queue
        _warmedJob = 0; // a fresh start pre-solves again (cached answers come back at once)
        if (ReferenceEquals(_queue.List, _characterQueue.List) && _queue.CanResume)
            _queue.Resume();
        else
            _queue.Start(_characterQueue.List, _characterQueue.Save);
        _stopAfterRun.Cancel("the queue was started");
    }

    /// <summary>Stop the queue and whatever run it has going. Positions are kept; both can be resumed.</summary>
    private void StopEverything()
    {
        _queue.Halt("stopped");
        _run.Halt("stopped");
        _desk.StopAll();
        _jobChange.Cancel();
        _gearFloor.Drop(); // here too: a Stop and a fresh start can both land between two ticks
    }

    /// <summary>Hephaestus.ChangeJob: only takes the request; the frame tick does the closing, the equipping and the watching.</summary>
    private bool ChangeJobForAnotherPlugin(uint jobId)
    {
        var pending = _jobChange.Pending;
        var accepted = _jobChange.Request(jobId, _jobs.CurrentJob, _jobs.HasGearsetFor(jobId), Busy);
        if (accepted && !pending && _jobChange.Pending)
            _log.Information("Hephaestus: another plugin asked for a change to job {0}", jobId);
        return accepted;
    }

    private void TickJobChange(DateTime now)
    {
        if (!_jobChange.Pending)
            return;

        switch (_jobChange.Tick(new JobChangeObservation(_jobs.CurrentJob, _observation.LogOpen, _jobs.CanSwitch), now))
        {
            case JobChangeCommand.CloseLog:
                _craftingLog.CloseLog();
                break;
            case JobChangeCommand.Equip:
                _jobs.Equip(_jobChange.Wanted); // accepted or not: the change is judged by the job itself
                break;
        }

        if (!_jobChange.Pending)
            _log.Information("Hephaestus: job change — {0}", _jobChange.Status);
    }

    /// <summary>
    /// Hephaestus.Stop. When it stops a request ANOTHER PLUGIN made, that plugin is taking the character back:
    /// Odysseus calls this the moment the items are in its bags — before the last synthesis has wound down —
    /// and walks off without waiting. So the work stops, but the crafting log is still closed and IsBusy
    /// stays true until the character can move. Anything else it stops, it stops like the Stop button.
    /// </summary>
    private void StopForAnotherPlugin()
    {
        if (!_oneOff || !_externalRequest || !_queue.Active)
        {
            StopEverything();
            return;
        }

        _log.Information("Hephaestus: Stop from the plugin that asked ({0}) — handing the character back", _queue.Status);
        _run.Halt("stopped");
        _desk.StopAll();
        _queue.HandBack("stopped by the plugin that asked.", DateTime.UtcNow);
    }

    /// <summary>
    /// Hephaestus.RequestStopAfterRun. Only takes the request: the next tick tells the queue and the run to
    /// start nothing more and stops the trips, and the stop then closes the crafting log once our work has ended.
    /// </summary>
    private bool RequestStopAfterRun()
    {
        var pending = _stopAfterRun.Pending;
        var accepted = _stopAfterRun.Request(Busy);
        if (accepted && !pending)
            _log.Information("Hephaestus: asked to stop after the run in progress ({0})", _queue.Active ? _queue.Status : _run.Status);
        return accepted;
    }

    private void TickStopAfterRun(DateTime now)
    {
        if (!_stopAfterRun.Pending)
            return;

        switch (_stopAfterRun.Tick(new StopObservation(Working, _observation.LogOpen, _jobs.CanSwitch), now))
        {
            case StopCommand.StopWork:
                _queue.StopAfterCurrentCraft(StopAfterRunReason);
                _run.StopAfterCraft(StopAfterRunReason);
                _desk.StopAll(); // a trip is not a craft: it stops where it is, as the Stop button stops it
                break;
            case StopCommand.CloseLog:
                _craftingLog.CloseLog();
                break;
        }

        if (!_stopAfterRun.Pending)
            _log.Information("Hephaestus: {0}", _stopAfterRun.Status);
    }

    /// <summary>
    /// Something of ours has the character, or will act on its own: the queue (between items too), a craft run,
    /// a trip, gathering we asked Demeter for, or a synthesis the solver is pressing in Auto.
    /// </summary>
    private bool Working
        => _queue.Active || _run.Active || _desk.Buying || _desk.Gathering || _desk.Withdrawing || _desk.FetchingFromChest
           || _jobChange.Pending
           || (_session.Active && _config.ModeFor(_account.LocalContentId) == ExecutionMode.Auto);

    /// <summary>Hephaestus.IsBusy: working, or still handing the character back after a stop-after-run.</summary>
    private bool Busy => Working || _stopAfterRun.Pending;

    private bool QueueOrRunActive() => _queue.Active || _run.Active;

    /// <summary>A buy or gather run started from the lists has the character.</summary>
    private bool SupplyBusy => !_queue.Active && (_desk.Buying || _desk.Gathering || _desk.Withdrawing || _desk.FetchingFromChest);

    private IReadOnlyList<ulong> HoldersOf(uint itemId) => _desk.HoldersOf(itemId);

    private IReadOnlyList<ulong> UnknownRetainerIds() => _desk.UnknownRetainerIds();

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events) => _desk.OnInventoryChanged();

    /// <summary>The button in the crafting log, and the right-click menu: put a recipe on the player's saved queue.</summary>
    private void AddToQueue(uint recipeId, int quantity)
    {
        if (_characterQueue.ContentId == 0 || _characterQueue.List.Add(recipeId, quantity) is not { } item)
            return;

        _characterQueue.Save();
        var name = _recipes.Read(recipeId)?.Name ?? $"recipe {recipeId}";
        Announce(item.Quantity == quantity
            ? $"Queued {quantity} × {name}."
            : $"Queued {quantity} more {name} — {item.Quantity - item.Crafted} to go.", warning: false);
    }

    /// <summary>Hephaestus.CraftItem: a one-off, unsaved queue of one item, run by the same engine as the player's own.</summary>
    private bool CraftItemForAnotherPlugin(uint recipeId, int amount)
    {
        if (amount < 1 || _recipes.Read(recipeId) == null)
            return false;

        var request = new CraftList { KeepOrder = true };
        request.Add(recipeId, amount);
        return StartOneOff(request, external: true);
    }

    /// <summary>
    /// Hephaestus.CraftSelected: N of the recipe selected in the open log - the ordinary one or a Cosmic mission's.
    /// On the job the character is on, the log stays open. A recipe for ANOTHER job goes the road the Craft X button
    /// takes for one (and <c>CraftItem</c> always takes): the queue engine changes job, makes it, and hands back.
    /// </summary>
    private bool CraftSelectedForAnotherPlugin(int amount)
    {
        var observation = _observation;
        if (amount < 1 || Busy || !observation.LogOpen || observation.SelectedRecipeId == 0)
            return false;
        if (_recipes.Read(observation.SelectedRecipeId) is not { } recipe)
            return false;

        if (recipe.JobId != _jobs.CurrentJob)
        {
            var request = new CraftList { KeepOrder = true };
            request.Add(observation.SelectedRecipeId, amount);
            return StartOneOff(request, external: true);
        }

        if (!_launcher.StartRequested(observation.SelectedRecipeId, observation.Craftable, amount))
            return false;

        _log.Information("Hephaestus: another plugin asked for {0} of the selected recipe ({1})", amount, recipe.Name);
        return true;
    }

    /// <summary>
    /// Craft All / Craft X on a recipe for a job the character is NOT on (what Artisan's craft button does
    /// too). The queue engine already knows how to get onto a job safely — close the log, equip the saved
    /// gearset, watch the job actually change, let stats settle — so the request becomes a one-off list of
    /// one item, the same road CraftItem takes. False = already on the job: the launcher starts the run itself.
    /// </summary>
    private bool CraftOnAnotherJob(uint recipeId, int count)
    {
        if (_recipes.Read(recipeId) is not { } recipe || recipe.JobId == _jobs.CurrentJob)
            return false;

        var request = new CraftList { KeepOrder = true };
        request.Add(recipeId, count);
        StartOneOff(request, external: false);
        return true; // taken either way: Synthesize is never pressed on the wrong job
    }

    /// <summary>Hephaestus.CraftList: the same, for a whole list the caller built.</summary>
    private bool CraftListForAnotherPlugin(string json)
        => ParseRequest(json, CraftListGateName) is { } list && StartOneOff(list, external: true);

    /// <summary>Hephaestus.AddToQueue: append the caller's list to the player's SAVED queue; nothing is started.</summary>
    private bool AddToQueueForAnotherPlugin(string json)
    {
        if (_characterQueue.ContentId == 0 || ParseRequest(json, AddToQueueGateName) is not { } list)
            return false;

        foreach (var item in list.Items)
            _characterQueue.List.Add(item.RecipeId, item.Quantity);

        _characterQueue.Save();
        Announce($"Another plugin added {list.Items.Count} item(s) to your queue.", warning: false);
        return true;
    }

    private bool StartQueueForAnotherPlugin()
    {
        StartQueue();
        return _queue.Active && !_oneOff;
    }

    private const string CraftListGateName = HephaestusIpc.CraftListGate;
    private const string AddToQueueGateName = HephaestusIpc.AddToQueueGate;

    /// <summary>All or nothing: a refused request says why in the log, so the calling plugin's author can fix it.</summary>
    private CraftList? ParseRequest(string json, string gate)
    {
        var parsed = ListRequest.Parse(
            json,
            recipeId => _recipes.Read(recipeId) != null,
            itemId => RecipeChoice.Pick(_recipes.RecipesFor(itemId), contextJob: 0, _jobs.CurrentJob)?.RecipeId);

        if (!parsed.Ok)
            _log.Warning("Hephaestus: {0} refused — {1}", gate, parsed.Error ?? "unknown");
        return parsed.List;
    }

    /// <summary>Run a one-off, unsaved list through the queue engine. IsBusy is already true when this returns true.</summary>
    /// <param name="external">Another plugin asked (reported as <c>External</c> in the status gate), not the player's own Craft All.</param>
    private bool StartOneOff(CraftList request, bool external)
    {
        if (_account.LocalContentId == 0 || _queue.Active || _run.Active || SupplyBusy)
            return false;

        _run.Clear();
        _oneOff = true;
        _externalRequest = external;
        // Another plugin moves the character the moment IsBusy falls (Odysseus never closes the log itself):
        // its request ends with the crafting log closed. The player's own Craft All keeps the log, as the game's does.
        _queue.Start(request, persist: null, leaveWhenDone: external);
        _stopAfterRun.Cancel("a new craft request was started");
        return true;
    }

    private string QueueStatusJson()
    {
        var item = _queue.CurrentItem;
        var flagged = _queue.List?.Items.Where(i => i.SkipReason != null)
            .Select(i => new FlaggedItemDto { RecipeId = i.RecipeId, Reason = i.SkipReason! }).ToArray();

        return JsonSerializer.Serialize(new QueueStatusDto
        {
            Active = _queue.Active || _run.Active,
            CanResume = _queue.CanResume || _run.CanResume,
            Status = QueueStatusDto.StatusLine(_queue.Active, _queue.List != null, _queue.Status, _run.Active, _run.CanResume, _run.Status),
            External = _externalRequest,
            CurrentRecipeId = item?.RecipeId ?? (_run.Active ? _run.RecipeId : 0),
            CurrentCrafted = item?.Crafted ?? _run.Completed,
            CurrentQuantity = item?.Quantity ?? _run.Requested,
            ItemsCompleted = _queue.ItemsCompleted,
            ItemsSkipped = _queue.ItemsSkipped,
            Flagged = flagged ?? Array.Empty<FlaggedItemDto>(),
            RunHalt = _run.HaltKind.ToString(),
            GearConditionPercent = _observation.MinGearConditionPercent,
            GearGatePercent = _config.Queue.GearConditionThresholdPercent,
            RunGearFloorPercent = _gearFloor.Percent < _config.Queue.GearConditionThresholdPercent ? _gearFloor.Percent : 0,
        });
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("workshop", StringComparison.OrdinalIgnoreCase))
            _workshopWindow.Toggle();
        else
            _mainWindow.Toggle();
    }

    private void OpenMainWindow() => _mainWindow.IsOpen = true;

    private void SaveConfig() => _pluginInterface.SavePluginConfig(_config);

    /// <summary>Queue a workshop part's materials across every phase, in phase order, as one saved-queue update.</summary>
    private void QueueWorkshopPart(uint partItemId)
    {
        if (_characterQueue.ContentId == 0)
            return;

        var build = _companyCraftReader.Build(partItemId);
        if (build is null)
            return;

        var phases = WorkshopQueue.Plan(build, itemId => RecipeChoice.Pick(_recipes.RecipesFor(itemId), contextJob: 0, _jobs.CurrentJob)?.RecipeId);
        var flat = WorkshopQueue.Flatten(phases).ToList();
        var notCraftable = phases.Sum(p => p.Lines.Count(l => l.RecipeId is null));

        foreach (var (recipeId, count) in flat)
            _characterQueue.List.Add(recipeId, count);

        _characterQueue.Save();

        var tail = notCraftable > 0 ? $", {notCraftable} not craftable — source by hand" : string.Empty;
        Announce($"Queued {build.PartName} — {build.Phases.Count} phases, {flat.Count} craft entries{tail}.", warning: false);
    }

    /// <summary>
    /// The craft-time log (the user, 2026-11-12: "a Hephaestus log that prints time for completed craft"): one line per
    /// synthesis that ended, to the chat window, the plugin log, and <c>craft-times/{content id}.log</c> under the
    /// config directory - a file per character, as every per-box file is. Never throws into the frame.
    /// </summary>
    private void OnSynthesisEnded(SynthesisRecord record)
    {
        try
        {
            var line = record.Describe(_craftingLog.RecipeName(record.RecipeId));
            _chat.Print($"[Hephaestus] {line}");
            _log.Info("Hephaestus craft: {0}", line);

            var contentId = _account.LocalContentId;
            if (contentId == 0)
                return;

            System.IO.Directory.CreateDirectory(_craftTimesDirectory);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(_craftTimesDirectory, $"{contentId:X16}.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {line}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            _log.Debug("Hephaestus: the craft-time log could not be written: {0}", ex.Message);
        }
    }

    /// <summary>A rated collectable is never "HQ": what is counted for it is whether it reached its top rating.</summary>
    private void OnCraftCompleted(uint recipeId, bool hq)
        => _outcomes.Record(_account.LocalContentId, recipeId, (_observation.CosmicLog || _run.InCosmicLog) && _session.LastTopRating is { } top ? top : hq);
}
