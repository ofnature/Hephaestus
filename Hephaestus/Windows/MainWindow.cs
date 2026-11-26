using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;
using Hephaestus.Ipc;
using Hephaestus.Services;
using Hephaestus.Services.Game;
using Hephaestus.Windows.Components;
using Hephaestus.Windows.Dock;
using ClassJob = Lumina.Excel.Sheets.ClassJob;
using Recipe = Lumina.Excel.Sheets.Recipe;

namespace Hephaestus.Windows;

public sealed class MainWindow : Window
{
    private static readonly string[] ModeLabels = { "Assisted (you press)", "Auto (plugin presses)" };
    private static readonly string[] OverrideLabels = { "Follow global", "Assisted (you press)", "Auto (plugin presses)" };
    private static readonly string[] AutoSourcingLabels = { "Follow account type", "On", "Off" };
    private static readonly string[] LandingPageLabels = { "Overview", "Queue", "Buy List", "Gather List", "Settings", "Debug" };

    private readonly HephaestusConfig _config;
    private readonly Action _saveConfig;
    private readonly Action _openWorkshop;
    private readonly AccountReader _account;
    private readonly CharacterQueue _queue;
    private readonly QueueRun _engine;
    private readonly Action _startQueue;
    private readonly Action _stopQueue;
    private readonly Func<bool> _otherWorkRunning;
    private readonly MaterialPlanner _materials;
    private readonly Func<uint> _currentJob;
    private readonly SupplyDesk _desk;
    private readonly VendorLocations _vendorLocations;
    private readonly IDataManager _data;
    private readonly Func<string> _solverStatus;
    private readonly Func<string> _expertStatus;
    private readonly Func<string> _plannerStatus;
    private static readonly string[] IntentLabels = { "Auto", "Quality first", "Bulk (time matters)" };
    private readonly Func<string> _raphaelStatus;
    private readonly Func<string> _warmupStatus;
    private readonly Func<string> _retainerStatus;
    private readonly Func<string> _stopAfterRunStatus;

    private int _addRecipeId;
    private int _addQuantity = 1;

    private MainPage _page;

    // Replanned only when the queue or the bags CHANGE (user, 2026-09-21: no scanning on a timer).
    private MaterialPlan _plan = MaterialPlan.Empty;
    private int _planSignature;
    private long _planInventoryVersion = -1;
    private DateTime _planAt;

    private bool _confirmClear;
    private DateTime _confirmClearAt;

    /// <param name="startQueue">Start (or resume) the engine on the character's queue.</param>
    /// <param name="stopQueue">Stop the engine and whatever craft run it has going.</param>
    /// <param name="otherWorkRunning">Something else has the character: a Craft All run, or another plugin's request.</param>
    public MainWindow(
        HephaestusConfig config,
        Action saveConfig,
        AccountReader account,
        CharacterQueue queue,
        QueueRun engine,
        Action startQueue,
        Action stopQueue,
        Func<bool> otherWorkRunning,
        MaterialPlanner materials,
        Func<uint> currentJob,
        SupplyDesk desk,
        VendorLocations vendorLocations,
        Func<string> retainerStatus,
        IDataManager data,
        Func<string> solverStatus,
        Func<string> raphaelStatus,
        Func<string> warmupStatus,
        Action openWorkshop,
        Func<string> stopAfterRunStatus,
        Func<string> expertStatus,
        Func<string> plannerStatus)
        : base("Hephaestus##HephaestusMain")
    {
        _config = config;
        _saveConfig = saveConfig;
        _openWorkshop = openWorkshop;
        _account = account;
        _queue = queue;
        _engine = engine;
        _startQueue = startQueue;
        _stopQueue = stopQueue;
        _otherWorkRunning = otherWorkRunning;
        _materials = materials;
        _currentJob = currentJob;
        _desk = desk;
        _vendorLocations = vendorLocations;
        _data = data;
        _solverStatus = solverStatus;
        _expertStatus = expertStatus;
        _plannerStatus = plannerStatus;
        _raphaelStatus = raphaelStatus;
        _warmupStatus = warmupStatus;
        _retainerStatus = retainerStatus;
        _stopAfterRunStatus = stopAfterRunStatus;

        _page = _config.LandingPage;

        Size = new Vector2(860, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    /// <summary>The edge dock's own Status line, for the Debug page.</summary>
    public Func<string> DockStatus { get; set; } = () => "off";

    /// <summary>The engine is working through THIS list (not another plugin's one-off request).</summary>
    private bool RunningThisQueue => _engine.Active && ReferenceEquals(_engine.List, _queue.List);

    public void ShowPage(MainPage target)
    {
        _page = target;
        IsOpen = true;
    }

    public override void Draw()
    {
        var contentId = _account.LocalContentId;

        using var style = HephaestusTheme.PushWindowStyle();

        var (status, accent, detail) = HeaderStatus(contentId);
        HephaestusHeader.Draw(status, accent, detail, () => _page = MainPage.Settings);

        var sidebarWidth = HephaestusTheme.SidebarWidth * ImGuiHelpers.GlobalScale;
        using (ImRaii.Child("##hephaestus_sidebar", new Vector2(sidebarWidth, -1), false))
            DrawSidebar();

        ImGui.SameLine();

        using (ImRaii.Child("##hephaestus_content", new Vector2(-1, -1), false))
        {
            switch (_page)
            {
                case MainPage.Overview: DrawOverview(contentId); break;
                case MainPage.Queue: DrawQueue(contentId); break;
                case MainPage.BuyList:
                    PageHeader("Buy List", "A snapshot of what the queue needs that NPC vendors sell — Needed against what is in your bags.");
                    SupplyListTab.Draw(_desk, SupplyKind.Buy);
                    break;
                case MainPage.GatherList:
                    PageHeader("Gather List", "What the queue needs that vendors do not cover, handed to Demeter to gather.");
                    SupplyListTab.Draw(_desk, SupplyKind.Gather);
                    break;
                case MainPage.Settings: DrawSettings(contentId); break;
                case MainPage.Debug: DrawDebug(contentId); break;
            }
        }
    }

    // ---- header / sidebar ----------------------------------------------------------------------

    private (string status, Vector4 accent, string detail) HeaderStatus(ulong contentId)
    {
        if (contentId == 0 || _queue.ContentId != contentId)
            return ("OFFLINE", HephaestusTheme.TextMuted, "Log in to a character");

        var list = _queue.List;
        var detail = $"Queue {list.Items.Count} · {list.TotalCrafted} / {list.TotalQuantity} crafted";

        if (RunningThisQueue)
            return ("CRAFTING", HephaestusTheme.StatusGreen, detail);
        if (_engine.CanResume && ReferenceEquals(_engine.List, list))
            return ("PAUSED", HephaestusTheme.AccentAmber, detail);
        return ("IDLE", HephaestusTheme.AccentGold, detail);
    }

    private void DrawSidebar()
    {
        var list = _queue.List;
        var queueBadge = list != null && list.Items.Count > 0 ? list.Items.Count.ToString() : null;
        var buyBadge = _desk.BuyList is { } buy && buy.Lines.Count > 0 ? buy.Lines.Count.ToString() : null;
        var gatherBadge = _desk.GatherList is { } gather && gather.Lines.Count > 0 ? gather.Lines.Count.ToString() : null;

        ImGui.Spacing();
        HephaestusTheme.SectionLabel("Overview");
        if (SidebarTab.Draw("Overview", FontAwesomeIcon.Home, _page == MainPage.Overview)) _page = MainPage.Overview;
        HephaestusTheme.SectionLabel("Crafting");
        if (SidebarTab.Draw("Queue", FontAwesomeIcon.List, _page == MainPage.Queue, queueBadge)) _page = MainPage.Queue;
        if (SidebarTab.Draw("Workshop", FontAwesomeIcon.Hammer, false)) _openWorkshop();
        HephaestusTheme.SectionLabel("Sourcing");
        if (SidebarTab.Draw("Buy List", FontAwesomeIcon.ShoppingCart, _page == MainPage.BuyList, buyBadge)) _page = MainPage.BuyList;
        if (SidebarTab.Draw("Gather List", FontAwesomeIcon.Leaf, _page == MainPage.GatherList, gatherBadge)) _page = MainPage.GatherList;
        HephaestusTheme.SectionLabel("System");
        if (SidebarTab.Draw("Settings", FontAwesomeIcon.Cog, _page == MainPage.Settings)) _page = MainPage.Settings;
        if (SidebarTab.Draw("Debug", FontAwesomeIcon.Bug, _page == MainPage.Debug)) _page = MainPage.Debug;
    }

    internal static void PageHeader(string title, string subtitle)
    {
        ImGui.SetWindowFontScale(1.45f);
        HephaestusTheme.Text(title, HephaestusTheme.TextPrimary);
        ImGui.SetWindowFontScale(1f);
        HephaestusTheme.TextWrapped(subtitle, HephaestusTheme.TextMuted);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    // ---- overview ------------------------------------------------------------------------------

    private void DrawOverview(ulong contentId)
    {
        PageHeader("Overview", "What the queue is doing right now — and what it needs from you.");

        if (contentId == 0 || _queue.ContentId != contentId)
        {
            HephaestusTheme.Text("Log in to a character to use the queue.", HephaestusTheme.TextSecondary);
            return;
        }

        var list = _queue.List;
        var total = list.TotalQuantity;
        var crafted = list.TotalCrafted;
        EnsurePlan(list);

        DrawQueueStats(list, total, crafted);

        HephaestusTheme.VSpace(4f);
        DrawQueueControls(list);

        HephaestusTheme.SectionLabel("Now");
        HephaestusTheme.VSpace(2f);

        var now = _engine.CurrentItem ?? list.Items.FirstOrDefault(i => !i.IsComplete);
        if (now != null)
        {
            var isCurrent = ReferenceEquals(now, _engine.CurrentItem) && RunningThisQueue;
            var origin = Card.BeginFlat();
            var width = ImGui.GetContentRegionAvail().X;
            var name = (isCurrent ? "> " : "") + RecipeName(now.RecipeId);
            HephaestusTheme.Text(name, isCurrent ? HephaestusTheme.AccentGold : HephaestusTheme.TextPrimary);
            var fraction = now.Quantity == 0 ? 0f : (float)now.Crafted / now.Quantity;
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{now.Crafted} / {now.Quantity}");
            var mode = _config.ModeFor(contentId) == ExecutionMode.Auto ? "Auto" : "Assisted";
            HephaestusTheme.Text($"{mode} — {(_config.Solver.Enabled ? "solver baseline cached" : "solver switched off")}", HephaestusTheme.TextMuted);
            Card.EndFlat(origin, width, accent: isCurrent ? HephaestusTheme.AccentGold : HephaestusTheme.BorderDim);
        }
        else
        {
            HephaestusTheme.Text("Nothing in the queue — add a recipe from the crafting log.", HephaestusTheme.TextMuted);
        }

        HephaestusTheme.SectionLabel("Coming up");
        HephaestusTheme.VSpace(2f);

        var coming = list.Items.Where(i => !i.IsComplete && !ReferenceEquals(i, now)).Take(3).ToList();
        if (coming.Count == 0)
        {
            HephaestusTheme.Text("Nothing else to make.", HephaestusTheme.TextMuted);
        }
        else
        {
            foreach (var item in coming)
                NextRow(RecipeName(item.RecipeId), $"{item.Crafted} / {item.Quantity}", item.SkipReason != null ? HephaestusTheme.AccentAmber : HephaestusTheme.TextDim);
        }

        HephaestusTheme.SectionLabel("Sourcing");
        HephaestusTheme.VSpace(2f);

        if (_plan.Shortfalls.Count == 0)
        {
            HephaestusTheme.Text("Everything the queue needs is in your bags.", HephaestusTheme.TextSecondary);
        }
        else
        {
            var shortNames = string.Join(" · ", _plan.Shortfalls.Select(s => $"{s.Missing} × {s.Name}"));
            HephaestusTheme.Text($"{_plan.Shortfalls.Count} item(s) short — not in your bags and not craftable from them:", HephaestusTheme.StatusYellow);
            HephaestusTheme.Text(shortNames, HephaestusTheme.TextSecondary);
            HephaestusTheme.VSpace(4f);
            ImGui.BeginDisabled(list.Items.All(i => i.IsComplete));
            if (Buttons.Action("Make buy list", true, 160f)) { _desk.Make(SupplyKind.Buy); _page = MainPage.BuyList; }
            ImGui.SameLine();
            if (Buttons.Action("Make gather list", true, 160f)) { _desk.Make(SupplyKind.Gather); _page = MainPage.GatherList; }
            ImGui.EndDisabled();
        }
    }

    private void NextRow(string name, string qty, Vector4 qtyColor)
    {
        HephaestusTheme.Text(name, HephaestusTheme.TextSecondary);
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(qty).X);
        HephaestusTheme.Text(qty, qtyColor);
    }

    // ---- queue ---------------------------------------------------------------------------------

    private void DrawQueue(ulong contentId)
    {
        PageHeader("Queue", "Works through the list unattended, changing job between items — a problem with one item flags it; a problem that needs you pauses the queue.");

        if (contentId == 0 || _queue.ContentId != contentId)
        {
            HephaestusTheme.Text("Log in to a character to edit its queue.", HephaestusTheme.TextSecondary);
            return;
        }

        var list = _queue.List;
        EnsurePlan(list);

        DrawQueueStats(list, list.TotalQuantity, list.TotalCrafted);

        HephaestusTheme.SectionLabel("Items");
        HephaestusTheme.VSpace(2f);

        DrawQueueControls(list);

        var locked = RunningThisQueue;
        var moveIndex = -1;
        var moveDirection = 0;
        var removeIndex = -1;

        foreach (var item in list.Items)
        {
            var i = list.Items.IndexOf(item);
            var current = ReferenceEquals(item, _engine.CurrentItem);
            var accent = current ? HephaestusTheme.AccentGold
                : item.IsComplete ? HephaestusTheme.StatusGreen
                : item.SkipReason != null ? HephaestusTheme.AccentAmber
                : HephaestusTheme.BorderDim;

            var origin = Card.BeginFlat();
            var width = ImGui.GetContentRegionAvail().X;

            var name = (current ? "> " : "") + RecipeName(item.RecipeId);
            HephaestusTheme.Text(name, current ? HephaestusTheme.AccentGold : HephaestusTheme.TextPrimary);
            DrawItemButtons(i, locked, ref moveIndex, ref moveDirection, ref removeIndex);

            if (item.SkipReason != null)
            {
                HephaestusTheme.Text("flagged: " + item.SkipReason, HephaestusTheme.StatusYellow);
                ImGui.SameLine();
                if (GhostButton("Retry"))
                {
                    item.SkipReason = null;
                    _queue.Save();
                }
            }

            var fraction = item.Quantity == 0 ? 0f : (float)item.Crafted / item.Quantity;
            ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{item.Crafted} / {item.Quantity}");

            if (_config.Queue.ExpandSubCrafts && item.IsRunnable && _plan.Achievable(item) < item.Quantity - item.Crafted)
            {
                HephaestusTheme.Text($"mats for {_plan.Achievable(item)}", HephaestusTheme.StatusYellow);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("How many of the remaining crafts your bags can see through to the end, sub-crafts included.\nSee Materials below for what is short.");
            }

            Card.EndFlat(origin, width, accent: accent);
        }

        if (moveIndex >= 0)
        {
            list.Move(moveIndex, moveDirection);
            _queue.Save();
        }
        else if (removeIndex >= 0)
        {
            list.Items.RemoveAt(removeIndex);
            _queue.Save();
        }

        DrawMaterials(list);
        DrawAdd(list, locked);
    }

    private void DrawItemButtons(int i, bool locked, ref int moveIndex, ref int moveDirection, ref int removeIndex)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - 78f * scale);
        ImGui.BeginDisabled(locked);
        if (GhostButton("▲")) (moveIndex, moveDirection) = (i, -1);
        ImGui.SameLine();
        if (GhostButton("▼")) (moveIndex, moveDirection) = (i, +1);
        ImGui.SameLine();
        if (GhostButton("✕")) removeIndex = i;
        ImGui.EndDisabled();
    }

    private void DrawQueueStats(CraftList list, int total, int crafted)
    {
        var job = JobAbbrev(_currentJob());
        var jobs = list.Items.Select(i => i.RecipeId).Distinct().Count();

        StatRow(
            ("In queue", list.Items.Count.ToString(), $"{jobs} recipes", HephaestusTheme.AccentGold),
            ("Crafted", $"{crafted} / {total}", total == 0 ? "—" : $"{100 * crafted / total}%", HephaestusTheme.AccentGold),
            ("Short", _plan.Shortfalls.Count.ToString(), "needs sourcing", HephaestusTheme.AccentAmber),
            ("Current job", job, "crafting class", HephaestusTheme.AccentGold));
    }

    private void DrawQueueControls(CraftList list)
    {
        if (RunningThisQueue)
        {
            HephaestusTheme.Text(_engine.Status, HephaestusTheme.StatusGreen);
            if (Buttons.Action("Stop", true, 100f)) _stopQueue();
            ImGui.SameLine();
            var keepOrder = list.KeepOrder;
            if (ImGui.Checkbox("Keep order", ref keepOrder)) { list.KeepOrder = keepOrder; _queue.Save(); }
            ImGui.SameLine();
            DrawClearQueue(list, disabled: true);
            return;
        }

        var resumable = _engine.CanResume && ReferenceEquals(_engine.List, list);
        HephaestusTheme.Text(
            ReferenceEquals(_engine.List, list) ? _engine.Status : "Idle — press Start to work through the list.",
            resumable ? HephaestusTheme.StatusYellow : HephaestusTheme.TextSecondary);

        var runnable = list.Items.Any(i => i.IsRunnable);
        var busyElsewhere = _otherWorkRunning();
        var canStart = runnable && !busyElsewhere;
        if (Buttons.Action(resumable ? "Resume" : "Start", canStart, 110f)) _startQueue();
        if (busyElsewhere && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("A Craft All run, a buy or gather run, or another plugin's request is using the character right now.");

        ImGui.SameLine();
        var keep = list.KeepOrder;
        if (ImGui.Checkbox("Keep order", ref keep)) { list.KeepOrder = keep; _queue.Save(); }
        HephaestusTheme.HelpMarker("Off: an item for the job you are already on may go ahead of its turn to save a gearset change — but never ahead of an item that makes one of its ingredients. On: strictly top to bottom.");

        ImGui.SameLine();
        if (GhostButton("Reset progress"))
        {
            foreach (var item in list.Items)
            {
                item.Crafted = 0;
                item.SkipReason = null;
            }

            _queue.Save();
        }

        ImGui.SameLine();
        DrawClearQueue(list, disabled: false);
    }

    /// <summary>What the queue will make first from what is in the bags, and what it is short of — then the Make buttons.</summary>
    private void DrawMaterials(CraftList list)
    {
        HephaestusTheme.SectionLabel("Materials");
        HephaestusTheme.VSpace(2f);

        ImGui.BeginDisabled(list.Items.All(i => i.IsComplete));
        if (Buttons.Action("Make buy list", true, 150f)) { _desk.Make(SupplyKind.Buy); _page = MainPage.BuyList; }
        ImGui.SameLine();
        if (Buttons.Action("Make gather list", true, 150f)) { _desk.Make(SupplyKind.Gather); _page = MainPage.GatherList; }
        ImGui.EndDisabled();
        HephaestusTheme.HelpMarker("Everything the queue still needs from NPC vendors, or (what they do not cover) from gathering — flagged entries included. Fetch it by hand, or with the button at the bottom of the list.");

        if (!_config.Queue.ExpandSubCrafts)
        {
            HephaestusTheme.Text("Material planning is switched off in Settings: entries are crafted exactly as listed.", HephaestusTheme.StatusGrey);
            return;
        }

        EnsurePlan(list);

        if (_plan.SubCrafts.Count == 0 && _plan.Shortfalls.Count == 0 && _plan.Withdrawals.Count == 0 && _plan.ChestWithdrawals.Count == 0)
        {
            HephaestusTheme.Text(list.Items.Any(i => i.IsRunnable)
                ? "Everything the queue needs is in your bags."
                : "Nothing left to plan.", HephaestusTheme.TextSecondary);
            return;
        }

        if (_plan.ChestWithdrawals.Count > 0)
        {
            HephaestusTheme.Text("From the company chest (as last seen), taken before crafting:", HephaestusTheme.TextPrimary);
            foreach (var withdrawal in _plan.ChestWithdrawals)
                HephaestusTheme.Text($"   {withdrawal.Quantity} × {withdrawal.Name}  —  for {string.Join(", ", withdrawal.ByEntry.Keys.Select(e => RecipeName(e.RecipeId)))}", HephaestusTheme.TextSecondary);

            if (_desk.FetchingFromChest)
            {
                HephaestusTheme.Text("   " + _desk.ChestStatus, HephaestusTheme.StatusGreen);
                ImGui.SameLine();
                if (GhostButton("Stop")) _desk.StopAll();
            }
            else
            {
                ImGui.BeginDisabled(!_desk.CanFetchFromChest(_plan.ChestWithdrawals));
                if (GhostButton("Take from chest now")) _desk.FetchFromChestNow(_plan.ChestWithdrawals);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Goes to the company chest (Lifestream to the estate if it is not nearby), views every page, and takes these — whole stacks, the smallest that covers each need.\nEach move is checked against the chest slot emptying. Bags full stops it.");
            }
        }

        if (_plan.Withdrawals.Count > 0)
        {
            HephaestusTheme.Text("From your retainers (as last seen), withdrawn before crafting:", HephaestusTheme.TextPrimary);
            foreach (var withdrawal in _plan.Withdrawals)
                HephaestusTheme.Text($"   {withdrawal.Quantity} × {withdrawal.Name}  —  for {string.Join(", ", withdrawal.ByEntry.Keys.Select(e => RecipeName(e.RecipeId)))}", HephaestusTheme.TextSecondary);

            if (_desk.Withdrawing)
            {
                HephaestusTheme.Text("   " + _desk.WithdrawStatus, HephaestusTheme.StatusGreen);
                ImGui.SameLine();
                if (GhostButton("Stop")) _desk.StopAll();
            }
            else
            {
                ImGui.BeginDisabled(!_desk.CanWithdraw(_plan.Withdrawals));
                if (GhostButton("Withdraw now")) _desk.WithdrawNow(_plan.Withdrawals);
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Walks (or teleports) to a summoning bell, opens the retainers the store says hold these, and withdraws them.\n"
                                     + (_desk.ViaCharon ? "The store is Charon's; it learns what our trip opens." : "The store is our own index, learned from any retainer you open.")
                                     + "\nEach withdrawal is checked against your bags. Bags full stops it.");
            }
        }

        if (_plan.SubCrafts.Count > 0)
        {
            HephaestusTheme.Text("Crafted first, automatically, from what is in your bags:", HephaestusTheme.TextPrimary);
            foreach (var subCraft in _plan.SubCrafts)
            {
                var making = _engine.CurrentSubCraft?.Recipe.RecipeId == subCraft.Recipe.RecipeId;
                var made = subCraft.Crafts * subCraft.Recipe.Yield;
                HephaestusTheme.Text($"{(making ? "> " : "   ")}{made} × {subCraft.Recipe.Name}  —  for {string.Join(", ", subCraft.For.Select(e => RecipeName(e.RecipeId)))}",
                    making ? HephaestusTheme.AccentGold : HephaestusTheme.TextSecondary);
            }
        }

        if (_plan.Shortfalls.Count > 0)
        {
            HephaestusTheme.Text("Short — not in your bags and not craftable from them:", HephaestusTheme.StatusYellow);
            foreach (var shortfall in _plan.Shortfalls)
                HephaestusTheme.Text($"   {shortfall.Missing} × {shortfall.Name}  —  for {string.Join(", ", shortfall.ByEntry.Keys.Select(e => RecipeName(e.RecipeId)))}", HephaestusTheme.TextSecondary);

            HephaestusTheme.Text(_config.AutoSourcingFor(_account.LocalContentId, _account.IsFreeTrial)
                ? "   Automatic buying and gathering is on for this character: the queue fetches these itself (Settings > Queue)."
                : "   The queue makes what it can, then flags the entry. Make a buy or gather list to fetch the rest.", HephaestusTheme.StatusGrey);
        }
    }

    private void DrawAdd(CraftList list, bool locked)
    {
        HephaestusTheme.SectionLabel("Add");
        HephaestusTheme.VSpace(2f);

        ImGui.BeginDisabled(locked);
        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("Recipe ID", ref _addRecipeId);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        ImGui.InputInt("Quantity", ref _addQuantity);
        _addQuantity = Math.Max(1, _addQuantity);

        var valid = _addRecipeId > 0 && RecipeExists((uint)_addRecipeId);
        ImGui.SameLine();
        if (Buttons.Action("Add", valid, 80f))
        {
            list.Items.Add(new CraftListItem { RecipeId = (uint)_addRecipeId, Quantity = _addQuantity });
            _queue.Save();
        }
        ImGui.EndDisabled();

        if (_addRecipeId > 0)
            HephaestusTheme.Text(valid ? RecipeName((uint)_addRecipeId) : "No such recipe.", valid ? HephaestusTheme.TextSecondary : HephaestusTheme.StatusYellow);
    }

    /// <summary>Cheap and game-free: changes whenever an entry is added, removed, edited or progresses.</summary>
    private void EnsurePlan(CraftList list)
    {
        var now = DateTime.UtcNow;
        var signature = Signature(list);
        if ((signature != _planSignature || _desk.InventoryVersion != _planInventoryVersion) && now >= _planAt)
        {
            _plan = _materials.Plan(list.Items, _currentJob(), retainers: _desk.RetainerSupply(), chest: _desk.ChestSupply());
            _planSignature = signature;
            _planInventoryVersion = _desk.InventoryVersion;
            _planAt = now + TimeSpan.FromSeconds(2); // a burst of inventory events replans once, not per event
        }
    }

    private static int Signature(CraftList list)
    {
        var hash = new HashCode();
        foreach (var item in list.Items)
        {
            hash.Add(item.RecipeId);
            hash.Add(item.Quantity);
            hash.Add(item.Crafted);
            hash.Add(item.SkipReason != null);
        }

        return hash.ToHashCode();
    }

    // ---- settings ------------------------------------------------------------------------------

    private void DrawSettings(ulong contentId)
    {
        PageHeader("Settings", "A global default, overridden per character. Per-box state lives in each character's queue, not here.");

        using (var window = SettingsGroup.Begin("Window"))
        {
            var landingIndex = (int)_config.LandingPage;
            window.Row("Landing page", "Which page opens first when you open the window.", 170f, () =>
            {
                ImGui.SetNextItemWidth(170f);
                if (ImGui.Combo("##landing", ref landingIndex, LandingPageLabels, LandingPageLabels.Length))
                {
                    _config.LandingPage = (MainPage)landingIndex;
                    _saveConfig();
                }
            });

            var docked = _config.EdgeDockEnabled;
            if (window.Toggle("A Hephaestus tab on the screen edge", "Point at the tab and this window pops out beside it; click the tab to pin it open; drag it to any edge. It shares the edges with the other plugins' tabs.", ref docked))
            {
                _config.EdgeDockEnabled = docked;
                _saveConfig();
            }

            if (docked)
            {
                window.Row("Tab position", "Dragging the tab moves it; this puts it back in the middle of the left edge.", 170f, () =>
                {
                    if (GhostButton("Back to the left edge"))
                    {
                        _config.EdgeDockEdge = DockEdge.Left;
                        _config.EdgeDockAlong = 0.5f;
                        _saveConfig();
                    }
                });
            }
        }

        using (var mode = SettingsGroup.Begin("Execution mode"))
        {
            var defaultMode = (int)_config.DefaultMode;
            mode.Row("Global default", "Auto for the multibox fleet; Assisted when you want to press the actions yourself.", 190f, () =>
            {
                ImGui.SetNextItemWidth(190f);
                if (ImGui.Combo("##modeGlobal", ref defaultMode, ModeLabels, ModeLabels.Length))
                {
                    _config.DefaultMode = (ExecutionMode)defaultMode;
                    _saveConfig();
                }
            });

            ImGui.BeginDisabled(contentId == 0);
            var current = contentId != 0 && _config.Characters.TryGetValue(contentId, out var existing) ? existing.ModeOverride : null;
            var overrideIndex = current is { } m ? (int)m + 1 : 0;
            mode.Row("This character", "Overrides the global default for the logged-in character only, e.g. Auto on alts and Assisted on your main.", 190f, () =>
            {
                ImGui.SetNextItemWidth(190f);
                if (ImGui.Combo("##modeChar", ref overrideIndex, OverrideLabels, OverrideLabels.Length))
                {
                    _config.CharacterFor(contentId).ModeOverride = overrideIndex == 0 ? null : (ExecutionMode)(overrideIndex - 1);
                    _saveConfig();
                }
            });
            ImGui.EndDisabled();
        }

        using (var queue = SettingsGroup.Begin("Queue"))
        {
            var retries = _config.Queue.MaxCraftRetries;
            queue.Row("Retries after a failed craft", "A failed synthesis loses its materials. After this many retries the item is flagged and skipped, and the queue continues.", 90f, () =>
            {
                ImGui.SetNextItemWidth(90f);
                if (ImGui.InputInt("##retries", ref retries)) { _config.Queue.MaxCraftRetries = Math.Clamp(retries, 0, 10); _saveConfig(); }
            });

            var threshold = _config.Queue.GearConditionThresholdPercent;
            queue.Row("Gear condition gate (%)", "Broken gear gives no stats. Below this, the queue pauses before the next craft and resumes where it stopped.", 160f, () =>
            {
                ImGui.SetNextItemWidth(160f);
                if (ImGui.SliderInt("##gear", ref threshold, 1, 99)) { _config.Queue.GearConditionThresholdPercent = threshold; _saveConfig(); }
            });

            var expand = _config.Queue.ExpandSubCrafts;
            if (queue.Toggle("Craft missing intermediates automatically (sub-crafts)", "Before an entry, the queue makes whatever it is short of and this character can craft — ingots before the shield — deepest first, only on jobs with a saved gearset and the level for the recipe, and only for crafts your materials can see through to the end. Off: no material planning at all.", ref expand))
            {
                _config.Queue.ExpandSubCrafts = expand;
                _saveConfig();
            }

            ImGui.BeginDisabled(contentId == 0);
            var chosen = contentId != 0 && _config.Characters.TryGetValue(contentId, out var character) ? character.AutoSourcing : null;
            var autoIndex = chosen switch { true => 1, false => 2, null => 0 };
            queue.Row("Buy & gather automatically (this character)", "On: when the queue runs short it buys from NPC vendors by itself — walking, or teleporting when none is near — and asks Demeter to gather what cannot be bought. Off: it flags what is short. The market board itself is never automated.", 170f, () =>
            {
                ImGui.SetNextItemWidth(170f);
                if (ImGui.Combo("##autoSourcing", ref autoIndex, AutoSourcingLabels, AutoSourcingLabels.Length))
                {
                    _config.CharacterFor(contentId).AutoSourcing = autoIndex switch { 1 => true, 2 => false, _ => null };
                    _saveConfig();
                }
            });
            ImGui.EndDisabled();
            queue.Note($"Now: {(_config.AutoSourcingFor(contentId, _account.IsFreeTrial) ? "on" : "off")} ({(_account.IsFreeTrial ? "free trial" : "paid account")}).");

            var contextMenu = _config.Queue.ContextMenu;
            if (queue.Toggle("Right-click menu: \"Add to Hephaestus Queue\"", "Shown for craftable items in inventories, the crafting log (recipes and their ingredients), chat links, the market board and shops.", ref contextMenu))
            {
                _config.Queue.ContextMenu = contextMenu;
                _saveConfig();
            }

            var vendorMenu = _config.Queue.VendorContextMenu;
            if (queue.Toggle("Right-click menu: \"Vendor Location\"", "Shown for items an NPC sells for gil: pick a seller and the map opens with a flag on them, and the price and place go to chat. Only gil shops - not scrips, seals or tomestones.", ref vendorMenu))
            {
                _config.Queue.VendorContextMenu = vendorMenu;
                _saveConfig();
            }

            ImGui.BeginDisabled();
            var autoRepair = _config.Queue.AutoRepair;
            var upkeep = _config.Queue.FoodPotionUpkeep;
            var extraction = _config.Queue.MateriaExtraction;
            queue.Toggle("Auto-repair", null, ref autoRepair);
            queue.Toggle("Food / potion upkeep", null, ref upkeep);
            queue.Toggle("Materia extraction", null, ref extraction);
            ImGui.EndDisabled();
            queue.Note("Not built yet — the queue pauses instead.");
        }

        using (var solver = SettingsGroup.Begin("Solver"))
        {
            var enabled = _config.Solver.Enabled;
            if (solver.Toggle("Solve crafts (recommend, and press in Auto mode)", "Off: Hephaestus neither recommends nor presses anything during a synthesis. Craft All still starts each craft.", ref enabled))
            {
                _config.Solver.Enabled = enabled;
                _saveConfig();
            }

            var highlight = _config.Solver.HighlightHotbar;
            if (solver.Toggle("Assisted mode: highlight the action on my hotbars", null, ref highlight))
            {
                _config.Solver.HighlightHotbar = highlight;
                _saveConfig();
            }

            var preSolve = _config.Solver.PreSolveQueue;
            if (solver.Toggle("Pre-solve the queue's recipes in the background", "When the queue starts or changes job, Raphael solves that job's coming crafts one at a time between syntheses, so the first craft of each recipe does not wait on the solver.", ref preSolve))
            {
                _config.Solver.PreSolveQueue = preSolve;
                _saveConfig();
            }

            var threads = _config.Solver.RaphaelThreads;
            solver.Row("Raphael threads (0 = half the cores)", "Used once per new recipe/stat combination; after that the baseline comes from the on-disk cache. Keep it low when several clients craft on one machine.", 90f, () =>
            {
                ImGui.SetNextItemWidth(90f);
                if (ImGui.InputInt("##threads", ref threads)) { _config.Solver.RaphaelThreads = Math.Clamp(threads, 0, 64); _saveConfig(); }
            });

            var expertSolver = _config.Solver.ExpertSolver;
            if (solver.Toggle("Expert recipes: use the expert solver", "Expert recipes are made by a solver that reacts to every condition and looks ahead over sampled futures, instead of following a Raphael plan. In Cosmic Exploration it also uses the mission's duty action. Off: expert recipes are solved like any other.", ref expertSolver))
            {
                _config.Solver.ExpertSolver = expertSolver;
                _saveConfig();
            }

            var expertTuning = _config.Solver.ExpertTuning;
            if (solver.Toggle("Expert recipes: tune for each recipe and my stats", "The first time an expert recipe is met, a second or two of simulated crafts on background threads finds the best settings for it at your stats. Off: presets only.", ref expertTuning))
            {
                _config.Solver.ExpertTuning = expertTuning;
                _saveConfig();
            }

            var raphaelPlans = _config.Solver.Planner == PlanSource.Raphael;
            if (solver.Toggle("Plan rotations with Raphael instead of our own planner", "Our own planner works inside the plugin: a few seconds the first time a recipe is met at your stats, then cached. With it, an item no rotation carries to full quality on paper is made by the expert solver. On: the bundled raphael-cli plans every ordinary recipe, as before.", ref raphaelPlans))
            {
                _config.Solver.Planner = raphaelPlans ? PlanSource.Raphael : PlanSource.Own;
                _saveConfig();
            }

            var intent = (int)_config.Solver.Intent;
            solver.Row("Hard crafts: quality or time", "When no rotation reaches full quality on paper, the expert solver makes the item. Quality first takes as long as it needs; bulk gives up a little quality to finish sooner. Auto: bulk when the run still has the number of crafts below to make.", 190f, () =>
            {
                ImGui.SetNextItemWidth(190f);
                if (ImGui.Combo("##intent", ref intent, IntentLabels, IntentLabels.Length)) { _config.Solver.Intent = (CraftIntent)intent; _saveConfig(); }
            });

            var bulkFrom = _config.Solver.BulkFrom;
            solver.Row("Auto: bulk from this many crafts", null, 90f, () =>
            {
                ImGui.SetNextItemWidth(90f);
                if (ImGui.InputInt("##bulkFrom", ref bulkFrom)) { _config.Solver.BulkFrom = Math.Clamp(bulkFrom, 2, 999); _saveConfig(); }
            });

            var budget = _config.Solver.LookaheadBudgetMs;
            solver.Row("Lookahead budget (ms)", "Runs on a background thread during the action's animation lock — it never stalls the game.", 160f, () =>
            {
                ImGui.SetNextItemWidth(160f);
                if (ImGui.SliderInt("##budget", ref budget, 50, 2000)) { _config.Solver.LookaheadBudgetMs = budget; _saveConfig(); }
            });

            var depth = _config.Solver.LookaheadMaxDepth;
            solver.Row("Lookahead max depth", null, 160f, () =>
            {
                ImGui.SetNextItemWidth(160f);
                if (ImGui.SliderInt("##depth", ref depth, 1, 8)) { _config.Solver.LookaheadMaxDepth = depth; _saveConfig(); }
            });

            var deviation = _config.Solver.BaselineDeviationPercent;
            solver.Row("Baseline deviation (%)", "Drops the Raphael baseline early when the craft drifts this far from its plan. It is always dropped once its remaining steps can no longer finish the craft.", 160f, () =>
            {
                ImGui.SetNextItemWidth(160f);
                if (ImGui.SliderFloat("##deviation", ref deviation, 5f, 100f, "%.0f")) { _config.Solver.BaselineDeviationPercent = deviation; _saveConfig(); }
            });
        }
    }

    // ---- debug ---------------------------------------------------------------------------------

    private void DrawDebug(ulong contentId)
    {
        PageHeader("Debug", "Every service's Status string — what it is doing, or why it is idle. Names are scrambled.");

        if (contentId == 0)
        {
            StatusLine("Character", "not logged in");
            return;
        }

        var freeTrial = _account.IsFreeTrial;

        StatusLine("Character", $"…{contentId % 0x10000:X4}");
        StatusLine("Account", freeTrial ? "FREE TRIAL — no FC chest, retainers or trading; 300k gil cap" : "Paid");
        StatusLine("Mode", _config.ModeFor(contentId).ToString());
        StatusLine("Material sources", string.Join(" > ", SourcePolicy.OrderFor(freeTrial)));
        StatusLine("Queue", _engine.Status);
        StatusLine("Stop after run", _stopAfterRunStatus());
        StatusLine("Solver", _config.Solver.Enabled ? _solverStatus() : "off — switched off in Settings");
        StatusLine("Expert solver", _expertStatus());
        StatusLine("Planner", _plannerStatus());
        StatusLine("Raphael", _raphaelStatus());
        StatusLine("Raphael warm-up", _warmupStatus());
        StatusLine("Vendor trip", _desk.BuyStatus);
        StatusLine("Gathering (Demeter)", _desk.GatherStatus);
        StatusLine("Vendor positions", _vendorLocations.Status);
        StatusLine("Retainers", _retainerStatus());
        StatusLine("Company chest", _desk.ChestSourceStatus);
        if (!_vendorLocations.Ready)
        {
            ImGui.SameLine();
            if (GhostButton("Read now")) _vendorLocations.EnsureStarted();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Reads where vendors stand from the game data, on a background thread (~0.6 s, ~170 MB measured outside the game).\nOnly reads files. If the game hitches or memory climbs when you press this, tell me — that is what this button is for.");
        }
        StatusLine("Edge dock", DockStatus());
        StatusLine("IPC", string.Join(", ", HephaestusIpc.Gates));
        StatusLine("Queue file", _queue.FilePath);
    }

    private static void StatusLine(string label, string value)
    {
        ImGui.TextColored(HephaestusTheme.AccentGold, label);
        ImGui.SameLine(140f);
        ImGui.TextWrapped(value);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static void StatRow(params (string label, string value, string sub, Vector4 accent)[] tiles)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var gap = 8f * scale;
        var width = (ImGui.GetContentRegionAvail().X - (tiles.Length - 1) * gap) / tiles.Length;
        for (var i = 0; i < tiles.Length; i++)
        {
            var (label, value, sub, accent) = tiles[i];
            StatTile.Draw(label, value, sub, accent, width);
            if (i < tiles.Length - 1)
                ImGui.SameLine(0, gap);
        }

        ImGui.NewLine();
    }

    private static bool GhostButton(string label)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Button, HephaestusTheme.CardBgSoft)
            .Push(ImGuiCol.ButtonHovered, HephaestusTheme.CardBgHover)
            .Push(ImGuiCol.ButtonActive, HephaestusTheme.CardBgHover)
            .Push(ImGuiCol.Text, HephaestusTheme.TextSecondary);
        return ImGui.SmallButton(label);
    }

    private static bool RoseButton(string label)
    {
        using var color = ImRaii.PushColor(ImGuiCol.Button, HephaestusTheme.AccentRose * 0.30f)
            .Push(ImGuiCol.ButtonHovered, HephaestusTheme.AccentRose * 0.45f)
            .Push(ImGuiCol.ButtonActive, HephaestusTheme.AccentRose * 0.60f)
            .Push(ImGuiCol.Text, HephaestusTheme.AccentRose);
        return ImGui.SmallButton(label);
    }

    /// <summary>"Clear queue": armed on the first click, clears on the second, disarms after six seconds.</summary>
    private void DrawClearQueue(CraftList list, bool disabled)
    {
        var armed = _confirmClear && DateTime.UtcNow - _confirmClearAt < TimeSpan.FromSeconds(6);
        var label = armed ? "Confirm clear?" : "Clear queue";

        ImGui.BeginDisabled(disabled || list.Items.Count == 0);
        var clicked = armed ? RoseButton(label) : GhostButton(label);
        if (clicked)
        {
            if (armed)
            {
                list.Items.Clear();
                _queue.Save();
                _confirmClear = false;
            }
            else
            {
                _confirmClear = true;
                _confirmClearAt = DateTime.UtcNow;
            }
        }

        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(armed
                ? "Click again to remove every entry from the queue. There is no undo."
                : "Remove every entry from the queue.");
    }

    private bool RecipeExists(uint recipeId)
        => _data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId) is { } recipe && recipe.ItemResult.RowId != 0;

    private string RecipeName(uint recipeId)
        => _data.GetExcelSheet<Recipe>().GetRowOrDefault(recipeId)?.ItemResult.ValueNullable?.Name.ExtractText()
           ?? $"recipe {recipeId}";

    private string JobAbbrev(uint jobId)
    {
        if (jobId == 0)
            return "—";
        var row = _data.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId);
        return row is { } r ? r.Abbreviation.ExtractText() : "—";
    }
}
