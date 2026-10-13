using Hephaestus.Features.Crafting;
using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

public class QueueRunTests
{
    private const uint Carpenter = 8;
    private const uint Blacksmith = 9;
    private const uint Armorer = 10;

    // recipe id -> (job, result item, ingredients)
    private static readonly Dictionary<uint, RecipeInfo> Recipes = new()
    {
        [100] = new(100, Carpenter, "Maple Lumber", ResultItemId: 5000, Array.Empty<RecipeInput>()),
        [101] = new(101, Carpenter, "Maple Shield", ResultItemId: 5001, new RecipeInput[] { new(5000, 1), new(6000, 1) }), // needs lumber + an ingot
        [200] = new(200, Blacksmith, "Bronze Ingot", ResultItemId: 6000, Array.Empty<RecipeInput>()),
        [201] = new(201, Blacksmith, "Bronze Hatchet", ResultItemId: 6001, new RecipeInput[] { new(6000, 1), new(5000, 1) }),
        [300] = new(300, Armorer, "Bronze Rivets", ResultItemId: 7000, new RecipeInput[] { new(6000, 1) }),
    };

    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
    private int _saves;

    private QueueRun Queue(params uint[] jobsWithGearsets)
    {
        var gearsets = jobsWithGearsets.Length == 0 ? new[] { Carpenter, Blacksmith, Armorer } : jobsWithGearsets;
        return new QueueRun(id => Recipes.TryGetValue(id, out var info) ? info : null, job => gearsets.Contains(job));
    }

    private static CraftList List(params (uint Recipe, int Quantity)[] items)
    {
        var list = new CraftList();
        foreach (var (recipe, quantity) in items)
            list.Items.Add(new CraftListItem { RecipeId = recipe, Quantity = quantity });
        return list;
    }

    private static QueueObservation Obs(
        uint job = Carpenter, bool runActive = false, int runCompleted = 0, CraftRunHalt halt = CraftRunHalt.None,
        bool logOpen = false, bool canSwitch = true, string runStatus = "")
        => new(job, canSwitch, logOpen, runActive, runCompleted, halt, runStatus);

    private QueueCommand Tick(QueueRun queue, QueueObservation obs, double advanceSeconds = 2)
    {
        _now = _now.AddSeconds(advanceSeconds);
        return queue.Tick(obs, _now);
    }

    /// <summary>Run the current item's craft run to a successful end: it reports active, counts up, then finishes.</summary>
    private void FinishCurrentItem(QueueRun queue, uint job, int count)
    {
        Tick(queue, Obs(job, runActive: true, runCompleted: 0));
        Tick(queue, Obs(job, runActive: true, runCompleted: count));
        Tick(queue, Obs(job, runActive: false, runCompleted: count));
    }

    [Fact]
    public void NotStarted_IsIdle_AndSaysSo()
    {
        var queue = Queue();

        Assert.False(queue.Active);
        Assert.False(queue.CanResume);
        Assert.Equal(QueueCommandKind.None, Tick(queue, Obs()).Kind);
        Assert.Contains("Idle", queue.Status);
    }

    [Fact]
    public void AnItemForTheCurrentJob_IsStartedForItsRemainingCount()
    {
        var queue = Queue();
        var list = List((100, 5));
        list.Items[0].Crafted = 2;
        queue.Start(list, null);

        var command = Tick(queue, Obs(Carpenter));

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 100, Count: 3), command);
        Assert.Same(list.Items[0], queue.CurrentItem);
    }

    [Fact]
    public void ProgressIsWrittenIntoTheList_AndPersisted_AsEachCraftLands()
    {
        var queue = Queue();
        var list = List((100, 3));
        queue.Start(list, () => _saves++);
        Tick(queue, Obs());

        Tick(queue, Obs(runActive: true, runCompleted: 1));
        Assert.Equal(1, list.Items[0].Crafted);
        Assert.Equal(1, _saves);

        Tick(queue, Obs(runActive: true, runCompleted: 1)); // nothing new: no pointless save
        Assert.Equal(1, _saves);

        Tick(queue, Obs(runActive: true, runCompleted: 3));
        Assert.Equal(3, list.Items[0].Crafted);
    }

    [Fact]
    public void AWholeQueue_AcrossTwoJobs_RunsToTheEnd()
    {
        var queue = Queue();
        var list = List((100, 2), (200, 1));
        queue.Start(list, null);

        Assert.Equal(QueueCommandKind.StartCraft, Tick(queue, Obs(Carpenter)).Kind);
        FinishCurrentItem(queue, Carpenter, 2);

        Assert.Equal(new QueueCommand(QueueCommandKind.SwitchJob, JobId: Blacksmith), Tick(queue, Obs(Carpenter)));
        Tick(queue, Obs(Blacksmith));
        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 200, Count: 1), Tick(queue, Obs(Blacksmith)));
        FinishCurrentItem(queue, Blacksmith, 1);

        Assert.Equal(QueueCommandKind.None, Tick(queue, Obs(Blacksmith)).Kind);
        Assert.False(queue.Active);
        Assert.False(queue.CanResume);
        Assert.Equal(2, queue.ItemsCompleted);
        Assert.Contains("Done", queue.Status);
    }

    // ---- job changes --------------------------------------------------------------------------------

    /// <summary>The game refuses a job change while the crafting log is open, so it is closed first.</summary>
    [Fact]
    public void TheCraftingLog_IsClosed_BeforeAJobChange()
    {
        var queue = Queue();
        queue.Start(List((200, 1)), null);

        Assert.Equal(QueueCommandKind.CloseLog, Tick(queue, Obs(Carpenter, logOpen: true)).Kind);
        Assert.Equal(QueueCommandKind.SwitchJob, Tick(queue, Obs(Carpenter, logOpen: false)).Kind);
    }

    [Fact]
    public void WhileAJobChangeWouldBeRefused_ItWaits()
    {
        var queue = Queue();
        queue.Start(List((200, 1)), null);

        Assert.Equal(QueueCommandKind.None, Tick(queue, Obs(Carpenter, canSwitch: false)).Kind);
        Assert.True(queue.Active);
        Assert.Contains("Waiting", queue.Status);
    }

    /// <summary>A missing gearset is a refusal — never an improvised gear swap. The item is flagged, the queue moves on.</summary>
    [Fact]
    public void NoGearsetForTheJob_FlagsTheItem_AndMovesOn()
    {
        var queue = Queue(Carpenter); // only a Carpenter gearset exists
        var list = List((200, 1), (100, 1));
        list.KeepOrder = true; // otherwise the Carpenter item would simply go first
        queue.Start(list, () => _saves++);

        Assert.Equal(QueueCommandKind.ClearRun, Tick(queue, Obs(Carpenter)).Kind);
        Assert.Contains("gearset", list.Items[0].SkipReason);
        Assert.True(_saves > 0);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 100, Count: 1), Tick(queue, Obs(Carpenter)));
    }

    [Fact]
    public void AJobChangeThatNeverHappens_IsRetried_ThenTheItemIsFlagged()
    {
        var queue = Queue();
        var list = List((200, 1));
        queue.Start(list, null);

        var switches = 0;
        for (var i = 0; i < 40 && queue.Active; i++)
        {
            if (Tick(queue, Obs(Carpenter), advanceSeconds: 4).Kind == QueueCommandKind.SwitchJob)
                switches++;
        }

        Assert.Equal(QueueRun.MaxSwitchAttempts, switches);
        Assert.Contains("could not change", list.Items[0].SkipReason);
    }

    [Fact]
    public void AfterAJobChange_ItLetsTheNewJobSettle_BeforeCrafting()
    {
        var queue = Queue();
        queue.Start(List((200, 1)), null);
        Tick(queue, Obs(Carpenter));
        Tick(queue, Obs(Blacksmith)); // the change landed

        Assert.Equal(QueueCommandKind.None, Tick(queue, Obs(Blacksmith), advanceSeconds: 0.2).Kind);
        Assert.Equal(QueueCommandKind.StartCraft, Tick(queue, Obs(Blacksmith), advanceSeconds: 2).Kind);
    }

    // ---- one item's problem never stops the queue -----------------------------------------------------

    [Theory]
    [InlineData(CraftRunHalt.OutOfMaterials, "materials")]
    [InlineData(CraftRunHalt.FailedRepeatedly, "failing")]
    [InlineData(CraftRunHalt.WouldNotStart, "would not start")]
    [InlineData(CraftRunHalt.LogUnresponsive, "would not open")]
    public void AProblemWithOneItem_FlagsIt_AndTheQueueCarriesOn(CraftRunHalt halt, string expectedReason)
    {
        var queue = Queue();
        var list = List((100, 5), (101, 1));
        queue.Start(list, null);
        Tick(queue, Obs());
        Tick(queue, Obs(runActive: true, runCompleted: 2));

        var command = Tick(queue, Obs(runActive: false, runCompleted: 2, halt: halt));

        Assert.Equal(QueueCommandKind.ClearRun, command.Kind);
        Assert.Contains(expectedReason, list.Items[0].SkipReason);
        Assert.Equal(2, list.Items[0].Crafted); // what was made still counts
        Assert.True(queue.Active);
        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 101, Count: 1), Tick(queue, Obs()));
    }

    [Fact]
    public void AFlaggedItem_IsNotRetried_UntilThePlayerClearsTheFlag()
    {
        var queue = Queue();
        var list = List((100, 1));
        queue.Start(list, null);
        Tick(queue, Obs());
        Tick(queue, Obs(runActive: true));
        Tick(queue, Obs(halt: CraftRunHalt.OutOfMaterials));
        Tick(queue, Obs());

        Assert.False(queue.Active);
        Assert.False(queue.CanResume);
        Assert.Contains("flagged", queue.Status);

        list.Items[0].SkipReason = null; // "Retry" in the UI
        Assert.True(queue.CanResume);
        queue.Resume();
        Assert.Equal(QueueCommandKind.StartCraft, Tick(queue, Obs()).Kind);
    }

    [Fact]
    public void AnUnknownRecipe_IsFlagged()
    {
        var queue = Queue();
        var list = List((99999, 1));
        queue.Start(list, null);

        Tick(queue, Obs());

        Assert.NotNull(list.Items[0].SkipReason);
    }

    // ---- a problem that needs a person always stops it ----------------------------------------------------

    [Theory]
    [InlineData(CraftRunHalt.GearGate)]
    [InlineData(CraftRunHalt.Cancelled)]
    [InlineData(CraftRunHalt.Stopped)]
    [InlineData(CraftRunHalt.InternalError)]
    public void AProblemThatNeedsAPerson_PausesTheQueue_WhereItIs(CraftRunHalt halt)
    {
        var queue = Queue();
        var list = List((100, 5), (101, 1));
        queue.Start(list, null);
        Tick(queue, Obs());
        Tick(queue, Obs(runActive: true, runCompleted: 2));

        Tick(queue, Obs(runActive: false, runCompleted: 2, halt: halt, runStatus: "gear is at 5% — repair, then resume"));

        Assert.False(queue.Active);
        Assert.True(queue.CanResume);
        Assert.Null(list.Items[0].SkipReason);
        Assert.Contains("Paused at Maple Lumber", queue.Status);
        Assert.Contains("repair", queue.Status);
    }

    [Fact]
    public void Resume_CarriesOnFromTheListsOwnProgress()
    {
        var queue = Queue();
        var list = List((100, 5));
        queue.Start(list, null);
        Tick(queue, Obs());
        Tick(queue, Obs(runActive: true, runCompleted: 2));
        Tick(queue, Obs(runCompleted: 2, halt: CraftRunHalt.GearGate));

        queue.Resume();

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 100, Count: 3), Tick(queue, Obs()));
    }

    [Fact]
    public void ARunThatNeverReportsActive_FlagsTheItem_InsteadOfWaitingForever()
    {
        var queue = Queue();
        var list = List((100, 1));
        queue.Start(list, null);
        Tick(queue, Obs());

        Tick(queue, Obs(runActive: false), advanceSeconds: 1);
        Assert.Null(list.Items[0].SkipReason);

        Tick(queue, Obs(runActive: false), advanceSeconds: 5);
        Assert.Contains("would not start", list.Items[0].SkipReason);
    }

    // ---- ordering ---------------------------------------------------------------------------------------

    /// <summary>Carpenter, Blacksmith, Carpenter in order is two job changes; taking both Carpenter items first is one.</summary>
    [Fact]
    public void AnItemForTheCurrentJob_MayGoFirst_ToSaveAJobChange()
    {
        var queue = Queue();
        queue.Start(List((200, 1), (100, 1)), null);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 100, Count: 1), Tick(queue, Obs(Carpenter)));
    }

    /// <summary>...but never ahead of an item that MAKES one of its ingredients — the sub-craft comes first, whatever it costs in job changes.</summary>
    [Fact]
    public void AnItem_NeverOvertakes_TheSubCraftItNeeds()
    {
        var queue = Queue();
        // The shield (Carpenter) needs the ingot (Blacksmith) that is queued ahead of it.
        queue.Start(List((200, 1), (101, 1)), null);

        Assert.Equal(new QueueCommand(QueueCommandKind.SwitchJob, JobId: Blacksmith), Tick(queue, Obs(Carpenter)));
    }

    [Fact]
    public void AnIndependentItem_StillGoesFirst_EvenWhenALaterOneIsBlocked()
    {
        var queue = Queue();
        // On Carpenter: ingot (BSM), shield (CRP, needs the ingot), lumber (CRP, needs nothing).
        queue.Start(List((200, 1), (101, 1), (100, 1)), null);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 100, Count: 1), Tick(queue, Obs(Carpenter)));
    }

    [Fact]
    public void KeepOrder_PinsTheList()
    {
        var queue = Queue();
        var list = List((200, 1), (100, 1));
        list.KeepOrder = true;
        queue.Start(list, null);

        Assert.Equal(QueueCommandKind.SwitchJob, Tick(queue, Obs(Carpenter)).Kind);
    }

    [Fact]
    public void FinishedAndFlaggedItems_AreSkippedOver()
    {
        var queue = Queue();
        var list = List((100, 1), (101, 1), (100, 2));
        list.Items[0].Crafted = 1;
        list.Items[1].SkipReason = "out of materials";
        queue.Start(list, null);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 100, Count: 2), Tick(queue, Obs(Carpenter)));
        Assert.Same(list.Items[2], queue.CurrentItem);
    }

    // ---- stopping ---------------------------------------------------------------------------------------

    [Fact]
    public void Halt_Pauses_Resumable_AndClearForgetsTheList()
    {
        var queue = Queue();
        queue.Start(List((100, 3)), null);
        Tick(queue, Obs());

        queue.Halt("stopped by you");
        Assert.False(queue.Active);
        Assert.True(queue.CanResume);
        Assert.Equal(QueueCommandKind.None, Tick(queue, Obs()).Kind);

        queue.Clear();
        Assert.False(queue.CanResume);
        Assert.Null(queue.List);
    }

    [Fact]
    public void AnEmptyList_FinishesAtOnce()
    {
        var queue = Queue();
        queue.Start(new CraftList(), null);

        Tick(queue, Obs());

        Assert.False(queue.Active);
        Assert.Contains("Done", queue.Status);
    }

    // ---- leaving the crafting stance, for a list another plugin asked for -------------------------------------

    /// <summary>The caller moves the character the moment the queue goes idle: not before the stance is left.</summary>
    [Fact]
    public void AListForAnotherPlugin_ClosesTheLogWhenDone_AndStaysActiveUntilTheCharacterIsFree()
    {
        var queue = Queue();
        queue.Start(List((100, 1)), null, leaveWhenDone: true);
        Tick(queue, Obs());
        FinishCurrentItem(queue, Carpenter, 1);

        var close = Tick(queue, Obs(logOpen: true, canSwitch: false));

        Assert.Equal(QueueCommandKind.CloseLog, close.Kind);
        Assert.Equal(QueuePhase.Leaving, queue.Phase);
        Assert.True(queue.Active);

        Tick(queue, Obs(logOpen: false, canSwitch: false)); // closed; the tools are still being put away
        Assert.True(queue.Active);

        Tick(queue, Obs(logOpen: false, canSwitch: true));  // free — believed once it has lasted
        Assert.True(queue.Active);
        Tick(queue, Obs(logOpen: false, canSwitch: true));

        Assert.False(queue.Active);
        Assert.Equal("Done — 1 item(s) crafted.", queue.Status);
    }

    /// <summary>The log is away for a moment after the last synthesis: looking free once is not being free.</summary>
    [Fact]
    public void LookingFreeForAMoment_IsNotBelieved_WhenTheLogComesBack()
    {
        var queue = Queue();
        queue.Start(List((100, 1)), null, leaveWhenDone: true);
        Tick(queue, Obs());
        FinishCurrentItem(queue, Carpenter, 1);

        Tick(queue, Obs(logOpen: false, canSwitch: true));
        Assert.True(queue.Active);

        Assert.Equal(QueueCommandKind.CloseLog, Tick(queue, Obs(logOpen: true, canSwitch: false)).Kind);
        Assert.True(queue.Active);
    }

    [Fact]
    public void ThePlayersOwnList_LeavesTheLogAlone()
    {
        var queue = Queue();
        queue.Start(List((100, 1)), null);
        Tick(queue, Obs());
        FinishCurrentItem(queue, Carpenter, 1);

        var command = Tick(queue, Obs(logOpen: true, canSwitch: false));

        Assert.Equal(QueueCommandKind.None, command.Kind);
        Assert.False(queue.Active);
    }

    /// <summary>A request that made nothing still hands back a character that can move.</summary>
    [Fact]
    public void AListForAnotherPlugin_ThatWasOnlyFlagged_LeavesTheStanceToo()
    {
        var queue = Queue(Carpenter); // no Blacksmith gearset
        var list = List((200, 1));
        queue.Start(list, null, leaveWhenDone: true);

        var closed = false;
        for (var i = 0; i < 6 && queue.Active && !closed; i++)
            closed = Tick(queue, Obs(Carpenter, logOpen: true, canSwitch: false)).Kind == QueueCommandKind.CloseLog;

        Assert.True(closed);
        Assert.True(queue.Active);
        Assert.NotNull(list.Items[0].SkipReason);
    }

    /// <summary>The work is done either way: a log that will not close is said, not waited on for ever.</summary>
    [Fact]
    public void ALogThatWillNotClose_EndsTheListAllTheSame_AndSaysSo()
    {
        var queue = Queue();
        queue.Start(List((100, 1)), null, leaveWhenDone: true);
        Tick(queue, Obs());
        FinishCurrentItem(queue, Carpenter, 1);

        var closes = 0;
        for (var i = 0; i < 40 && queue.Active; i++)
        {
            if (Tick(queue, Obs(logOpen: true, canSwitch: false), advanceSeconds: 0.7).Kind == QueueCommandKind.CloseLog)
                closes++;
        }

        Assert.False(queue.Active);
        Assert.Equal(QueueRun.MaxCloseAttempts, closes);
        Assert.StartsWith("Done — 1 item(s) crafted.", queue.Status);
        Assert.Contains("the crafting log would not close", queue.Status);
        Assert.False(queue.CanResume);
    }

    /// <summary>Closed, but the stance never ends (something else holds the character): a deadline, not a hang.</summary>
    [Fact]
    public void AStanceThatIsNeverLeft_EndsOnTheDeadline()
    {
        var queue = Queue();
        queue.Start(List((100, 1)), null, leaveWhenDone: true);
        Tick(queue, Obs());
        FinishCurrentItem(queue, Carpenter, 1);

        for (var i = 0; i < 40 && queue.Active; i++)
            Tick(queue, Obs(logOpen: false, canSwitch: false));

        Assert.False(queue.Active);
        Assert.Contains("could not be left in time", queue.Status);
    }

    [Fact]
    public void StopWhileLeaving_IsNotAPause_TheWorkIsDone()
    {
        var queue = Queue();
        queue.Start(List((100, 1)), null, leaveWhenDone: true);
        Tick(queue, Obs());
        FinishCurrentItem(queue, Carpenter, 1);
        Tick(queue, Obs(logOpen: true, canSwitch: false));

        queue.Halt("stopped");

        Assert.False(queue.Active);
        Assert.False(queue.CanResume);
        Assert.Equal("Done — 1 item(s) crafted.", queue.Status);
    }

    /// <summary>A pause that needs a person is not an ending: nothing is clicked for a list that stopped half way.</summary>
    [Fact]
    public void APauseThatNeedsAPerson_DoesNotTouchTheLog()
    {
        var queue = Queue();
        queue.Start(List((100, 2)), null, leaveWhenDone: true);
        Tick(queue, Obs());
        Tick(queue, Obs(runActive: true, runCompleted: 1));

        var command = Tick(queue, Obs(runActive: false, runCompleted: 1, halt: CraftRunHalt.GearGate, logOpen: true, canSwitch: false, runStatus: "gear needs repair"));

        Assert.Equal(QueueCommandKind.None, command.Kind);
        Assert.False(queue.Active);
        Assert.True(queue.CanResume);
    }

    /// <summary>
    /// What Odysseus does: the items are in its bags, so it says Stop — while the last synthesis is still
    /// winding down — and walks off. The character has to be able to walk.
    /// </summary>
    [Fact]
    public void TheCallersStop_EndsTheWork_ButStillLeavesTheCraftingStance()
    {
        var queue = Queue();
        queue.Start(List((100, 2)), null, leaveWhenDone: true);
        Tick(queue, Obs());
        Tick(queue, Obs(runActive: true, runCompleted: 1));

        queue.HandBack("stopped by the plugin that asked.", _now);

        Assert.True(queue.Active);
        Assert.Equal(QueuePhase.Leaving, queue.Phase);

        Assert.Equal(QueueCommandKind.None, Tick(queue, Obs(logOpen: false, canSwitch: false)).Kind); // the log is not back yet
        Assert.Equal(QueueCommandKind.CloseLog, Tick(queue, Obs(logOpen: true, canSwitch: false)).Kind);
        Assert.True(queue.Active);

        Tick(queue, Obs(logOpen: false, canSwitch: true));
        Tick(queue, Obs(logOpen: false, canSwitch: true));

        Assert.False(queue.Active);
        Assert.Equal("Stopped at Maple Lumber — stopped by the plugin that asked.", queue.Status);
    }

    /// <summary>The same Stop, a moment later: the list had run out and the log was being closed. It still gets closed.</summary>
    [Fact]
    public void TheCallersStop_WhileLeaving_DoesNotCutItShort()
    {
        var queue = Queue();
        queue.Start(List((100, 1)), null, leaveWhenDone: true);
        Tick(queue, Obs());
        FinishCurrentItem(queue, Carpenter, 1);
        Assert.Equal(QueueCommandKind.CloseLog, Tick(queue, Obs(logOpen: true, canSwitch: false)).Kind);

        queue.HandBack("stopped by the plugin that asked.", _now);

        Assert.True(queue.Active);
        Assert.Equal(QueueCommandKind.CloseLog, Tick(queue, Obs(logOpen: true, canSwitch: false)).Kind); // the first click did not take
        Tick(queue, Obs(logOpen: false, canSwitch: true));
        Tick(queue, Obs(logOpen: false, canSwitch: true));

        Assert.False(queue.Active);
        Assert.Equal("Done — 1 item(s) crafted.", queue.Status);
    }

    [Fact]
    public void HandBack_OnAListNobodyElseAskedFor_IsAPause()
    {
        var queue = Queue();
        queue.Start(List((100, 2)), null);
        Tick(queue, Obs());
        Tick(queue, Obs(runActive: true, runCompleted: 1));

        queue.HandBack("stopped", _now);

        Assert.False(queue.Active);
        Assert.True(queue.CanResume);
        Assert.StartsWith("Paused at Maple Lumber", queue.Status);
    }

    /// <summary>Gear that cannot safely be chosen for the player is that ITEM's problem: flagged with the reason, queue goes on.</summary>
    [Fact]
    public void AGearMaterialThatCouldNotBeChosen_FlagsTheItemWithTheReason_AndMovesOn()
    {
        var queue = Queue();
        var list = List((101, 1), (100, 1));
        queue.Start(list, null);
        Tick(queue, Obs());
        Tick(queue, Obs(runActive: true));

        const string why = "a Maple Shield with materia melded could be the one used up";
        var command = Tick(queue, Obs(halt: CraftRunHalt.MaterialChoice) with { RunHaltReason = why });

        Assert.Equal(QueueCommandKind.ClearRun, command.Kind);
        Assert.Equal(why, list.Items[0].SkipReason);
        Assert.True(queue.Active);
        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: 100, Count: 1), Tick(queue, Obs()));
    }
}
