using Hephaestus.Features.Crafting;

namespace Hephaestus.Tests.Features.Crafting;

public class CraftRunTests
{
    private const uint Recipe = 1234;

    private static readonly CraftRunSettings Settings = new(MaxCraftRetries: 2, GearConditionThresholdPercent: 20);

    private DateTime _now = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The log open on our recipe, materials assigned, gear healthy, nothing in the bags yet.</summary>
    private static CraftRunObservation ReadyInLog(int resultCount = 0) => new(
        LogOpen: true,
        SelectedRecipeId: Recipe,
        Craftable: 10,
        IngredientsAssigned: true,
        SynthesisOpen: false,
        Transitioning: false,
        Progress: 0,
        MaxProgress: 0,
        Durability: 0,
        ResultItemCount: resultCount,
        MinGearConditionPercent: 90);

    private static CraftRunObservation Synthesizing(int progress, int durability, int resultCount = 0) =>
        ReadyInLog(resultCount) with
        {
            LogOpen = false,
            SynthesisOpen = true,
            Progress = progress,
            MaxProgress = 100,
            Durability = durability,
        };

    private CraftRunCommand Tick(CraftRun run, CraftRunObservation obs, double advanceSeconds = 1)
    {
        _now = _now.AddSeconds(advanceSeconds);
        return run.Tick(obs, _now);
    }

    /// <summary>Drive one whole successful craft; the bag count goes from <paramref name="before"/> to before+1.</summary>
    private void CompleteOneCraft(CraftRun run, int before)
    {
        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog(before)));
        Tick(run, Synthesizing(0, 40, before));
        Tick(run, Synthesizing(100, 10, before));
        Tick(run, ReadyInLog(before) with { Transitioning = true });
        Tick(run, ReadyInLog(before + 1));
    }

    // ---- the Cosmic Crafting Log: a mission may take the item as it is made ----

    [Fact]
    public void InTheCosmicLog_ASynthesisThatFilledItsProgress_IsACraftMade_ThoughNothingReachesTheBags()
    {
        var run = new CraftRun();
        run.Start(Recipe, 2, Settings);
        var cosmic = ReadyInLog() with { CosmicLog = true };
        var made = new List<bool>();
        run.CraftCompleted += (_, hq) => made.Add(hq);

        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, cosmic));
        Tick(run, Synthesizing(0, 40));
        Tick(run, Synthesizing(100, 10));
        Tick(run, cosmic with { Transitioning = true });
        Assert.Equal(0, run.Completed);          // not while the finish is still playing
        Tick(run, cosmic);                       // the bags say nothing arrived - and there is no waiting for them: a mission is on the clock

        Assert.Equal(1, run.Completed);
        Assert.Single(made);
        Assert.True(run.Active);
        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, cosmic)); // and the next one starts
    }

    [Fact]
    public void InTheCosmicLog_ASynthesisThatBroke_IsStillAFailure_AndOneThatWasQuitStillStopsTheRun()
    {
        var broke = new CraftRun();
        broke.Start(Recipe, 2, Settings);
        var cosmic = ReadyInLog() with { CosmicLog = true };
        Tick(broke, cosmic);
        Tick(broke, Synthesizing(60, 0));
        Tick(broke, cosmic);
        Tick(broke, cosmic, advanceSeconds: 10);
        Assert.Equal(0, broke.Completed);
        Assert.Equal(1, broke.Failed);
        Assert.True(broke.Active);

        var quit = new CraftRun();
        quit.Start(Recipe, 2, Settings);
        Tick(quit, cosmic);
        Tick(quit, Synthesizing(60, 20));
        Tick(quit, cosmic);
        Tick(quit, cosmic, advanceSeconds: 10);
        Assert.Equal(CraftRunHalt.Cancelled, quit.HaltKind);
    }

    [Fact]
    public void StopAfterThisCraft_SaysSoWhileItWaits_FinishesTheCraftInHand_ThenPausesResumable()
    {
        var run = new CraftRun();
        run.Start(Recipe, 5, Settings);
        Assert.False(run.StoppingAfterCraft);

        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog()));
        Tick(run, Synthesizing(0, 40));
        run.StopAfterCraft("stopped by you after the craft in hand");
        Assert.True(run.StoppingAfterCraft);
        Assert.True(run.Active); // the craft in hand goes on

        Tick(run, Synthesizing(100, 10));
        Tick(run, ReadyInLog(1));
        Assert.Equal(1, run.Completed);
        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog(1))); // no second Synthesize

        Assert.False(run.Active);
        Assert.False(run.StoppingAfterCraft);
        Assert.True(run.CanResume);
        Assert.Equal(CraftRunHalt.Stopped, run.HaltKind);
    }

    [Fact]
    public void InTheOrdinaryLog_AFullProgressBarAloneIsNotAnItem()
    {
        var run = new CraftRun();
        run.Start(Recipe, 2, Settings);
        Tick(run, ReadyInLog());
        Tick(run, Synthesizing(100, 10));
        Tick(run, ReadyInLog());
        Tick(run, ReadyInLog(), advanceSeconds: 10);

        Assert.Equal(0, run.Completed);
        Assert.Equal(CraftRunHalt.Cancelled, run.HaltKind); // the verdict there is the item itself
    }

    [Fact]
    public void NewRun_IsIdleAndSaysSo()
    {
        var run = new CraftRun();

        Assert.False(run.Active);
        Assert.False(run.CanResume);
        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog()));
        Assert.Contains("Idle", run.Status);
    }

    [Theory]
    [InlineData(0u, 5)]
    [InlineData(Recipe, 0)]
    [InlineData(Recipe, -3)]
    public void Start_WithNothingToDo_StaysIdle(uint recipeId, int count)
    {
        var run = new CraftRun();

        run.Start(recipeId, count, Settings);

        Assert.False(run.Active);
    }

    [Fact]
    public void Preparing_WrongRecipeSelected_OpensOurs()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);

        Assert.Equal(CraftRunCommand.OpenRecipe, Tick(run, ReadyInLog() with { SelectedRecipeId = 999 }));
    }

    [Fact]
    public void Preparing_LogClosed_OpensTheRecipe()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);

        Assert.Equal(CraftRunCommand.OpenRecipe, Tick(run, ReadyInLog() with { LogOpen = false, SelectedRecipeId = 0 }));
    }

    [Fact]
    public void Preparing_MaterialsNotAssigned_AssignsBeforeSynthesizing()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);

        Assert.Equal(CraftRunCommand.AssignIngredients, Tick(run, ReadyInLog() with { IngredientsAssigned = false }));
        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog()));
    }

    [Fact]
    public void Commands_ArePaced()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);
        var notAssigned = ReadyInLog() with { IngredientsAssigned = false };

        Assert.Equal(CraftRunCommand.AssignIngredients, Tick(run, notAssigned));
        Assert.Equal(CraftRunCommand.None, Tick(run, notAssigned, advanceSeconds: 0.1));
        Assert.Equal(CraftRunCommand.AssignIngredients, Tick(run, notAssigned, advanceSeconds: 1));
    }

    [Fact]
    public void FullRun_CraftsTheRequestedCount_ThenStops()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);

        CompleteOneCraft(run, before: 0);
        Assert.Equal(1, run.Completed);
        Assert.True(run.Active);

        CompleteOneCraft(run, before: 1);
        CompleteOneCraft(run, before: 2);

        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog(3)));
        Assert.Equal(3, run.Completed);
        Assert.False(run.Active);
        Assert.False(run.CanResume);
        Assert.Contains("Done", run.Status);
    }

    /// <summary>Completion is the item arriving, not the Synthesis window closing.</summary>
    [Fact]
    public void Finishing_WaitsForTheFinishAnimation_BeforeJudging()
    {
        var run = new CraftRun();
        run.Start(Recipe, 1, Settings);
        Tick(run, ReadyInLog());
        Tick(run, Synthesizing(100, 10));

        // Window gone, animation still playing, item not in the bags yet — for a long time.
        for (var i = 0; i < 10; i++)
            Tick(run, ReadyInLog() with { Transitioning = true });

        Assert.Equal(CraftRunPhase.Finishing, run.Phase);
        Assert.Equal(0, run.Completed);
    }

    [Fact]
    public void Finishing_ItemLandsALittleLate_StillCounts()
    {
        var run = new CraftRun();
        run.Start(Recipe, 1, Settings);
        Tick(run, ReadyInLog());
        Tick(run, Synthesizing(100, 10));
        Tick(run, ReadyInLog());

        Tick(run, ReadyInLog(), advanceSeconds: 1);
        Assert.Equal(CraftRunPhase.Finishing, run.Phase);

        Tick(run, ReadyInLog(resultCount: 1), advanceSeconds: 1);
        Assert.Equal(1, run.Completed);
    }

    [Fact]
    public void FailedSynthesis_RetriesUpToTheCap_ThenHaltsResumable()
    {
        var run = new CraftRun();
        run.Start(Recipe, 5, Settings);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog()));
            Tick(run, Synthesizing(progress: 60, durability: 0));
            Tick(run, ReadyInLog());
            Tick(run, ReadyInLog(), advanceSeconds: 5);
        }

        // Two retries allowed: the third failure in a row stops the run.
        Assert.False(run.Active);
        Assert.Equal(3, run.Failed);
        Assert.Equal(0, run.Completed);
        Assert.True(run.CanResume);
        Assert.Contains("failed 3 times", run.Status);
    }

    [Fact]
    public void ASuccess_ResetsTheFailureStreak()
    {
        var run = new CraftRun();
        run.Start(Recipe, 5, Settings);

        for (var i = 0; i < 2; i++)
        {
            Tick(run, ReadyInLog());
            Tick(run, Synthesizing(60, 0));
            Tick(run, ReadyInLog());
            Tick(run, ReadyInLog(), advanceSeconds: 5);
        }

        CompleteOneCraft(run, before: 0);

        for (var i = 0; i < 2; i++)
        {
            Tick(run, ReadyInLog(1));
            Tick(run, Synthesizing(60, 0, 1));
            Tick(run, ReadyInLog(1));
            Tick(run, ReadyInLog(1), advanceSeconds: 5);
        }

        Assert.True(run.Active);
        Assert.Equal(4, run.Failed);
        Assert.Equal(1, run.Completed);
    }

    /// <summary>The window vanished with durability left and no item: the player cancelled. Never retry that.</summary>
    [Fact]
    public void CancelledCraft_HaltsImmediately()
    {
        var run = new CraftRun();
        run.Start(Recipe, 5, Settings);
        Tick(run, ReadyInLog());
        Tick(run, Synthesizing(progress: 30, durability: 40));
        Tick(run, ReadyInLog());
        Tick(run, ReadyInLog(), advanceSeconds: 5);

        Assert.False(run.Active);
        Assert.Equal(0, run.Failed);
        Assert.Contains("cancelled", run.Status);
    }

    /// <summary>Broken gear gives no stats — pause BEFORE the craft, and resume from the same place.</summary>
    [Fact]
    public void GearBelowGate_HaltsBeforeCrafting_AndResumesWhereItStopped()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);
        CompleteOneCraft(run, before: 0);

        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog(1) with { MinGearConditionPercent = 5 }));
        Assert.False(run.Active);
        Assert.True(run.CanResume);
        Assert.Contains("gear", run.Status);

        run.Resume(Settings);
        Assert.True(run.Active);
        Assert.Equal(1, run.Completed);
        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog(1)));
    }

    [Fact]
    public void OutOfMaterials_Halts()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);

        Tick(run, ReadyInLog() with { Craftable = 0, IngredientsAssigned = false });

        Assert.False(run.Active);
        Assert.Contains("out of materials", run.Status);
    }

    [Fact]
    public void CraftableCountUnreadable_WaitsInsteadOfGuessing()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);

        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog() with { Craftable = -1 }));
        Assert.True(run.Active);
    }

    [Fact]
    public void SynthesizeNeverOpensTheWindow_GivesUpAfterRepeatedFailedStarts()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);

        for (var i = 0; i < CraftRun.MaxFailedStarts; i++)
        {
            Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog()));
            Tick(run, ReadyInLog(), advanceSeconds: 10);
        }

        Assert.False(run.Active);
        Assert.Contains("would not start", run.Status);
    }

    [Fact]
    public void RecipeNeverOpens_HaltsInsteadOfSpinningForever()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);
        var stuck = ReadyInLog() with { LogOpen = false, SelectedRecipeId = 0 };

        for (var i = 0; i < CraftRun.MaxRepeats + 2 && run.Active; i++)
            Tick(run, stuck);

        Assert.False(run.Active);
        Assert.Contains("could not open the recipe", run.Status);
    }

    /// <summary>Reloading the plugin mid-craft, or starting the first craft by hand, must not confuse the run.</summary>
    [Fact]
    public void CraftAlreadyInProgress_IsAdopted()
    {
        var run = new CraftRun();
        run.Start(Recipe, 2, Settings);

        Assert.Equal(CraftRunCommand.None, Tick(run, Synthesizing(20, 40)));
        Assert.Equal(CraftRunPhase.Crafting, run.Phase);

        Tick(run, Synthesizing(100, 10));
        Tick(run, ReadyInLog(resultCount: 1));
        Assert.Equal(1, run.Completed);
    }

    [Fact]
    public void Halt_KeepsPosition_ClearForgetsIt()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);
        CompleteOneCraft(run, before: 0);

        run.Halt("stopped by you");
        Assert.True(run.CanResume);
        Assert.Equal(1, run.Completed);

        run.Clear();
        Assert.False(run.CanResume);
        Assert.Equal(0u, run.RecipeId);
    }

    /// <summary>The queue decides "skip this item" vs "stop everything" from this, so each ending must name itself.</summary>
    [Fact]
    public void EveryWayOfStopping_SaysWhichItWas()
    {
        var gear = new CraftRun();
        gear.Start(Recipe, 3, Settings);
        Tick(gear, ReadyInLog() with { MinGearConditionPercent = 5 });
        Assert.Equal(CraftRunHalt.GearGate, gear.HaltKind);

        var materials = new CraftRun();
        materials.Start(Recipe, 3, Settings);
        Tick(materials, ReadyInLog() with { Craftable = 0 });
        Assert.Equal(CraftRunHalt.OutOfMaterials, materials.HaltKind);

        var cancelled = new CraftRun();
        cancelled.Start(Recipe, 3, Settings);
        Tick(cancelled, ReadyInLog());
        Tick(cancelled, Synthesizing(30, 40));
        Tick(cancelled, ReadyInLog());
        Tick(cancelled, ReadyInLog(), advanceSeconds: 5);
        Assert.Equal(CraftRunHalt.Cancelled, cancelled.HaltKind);

        var stopped = new CraftRun();
        stopped.Start(Recipe, 3, Settings);
        stopped.Halt("stopped by you");
        Assert.Equal(CraftRunHalt.Stopped, stopped.HaltKind);

        var stuck = new CraftRun();
        stuck.Start(Recipe, 3, Settings);
        for (var i = 0; i < CraftRun.MaxRepeats + 2 && stuck.Active; i++)
            Tick(stuck, ReadyInLog() with { LogOpen = false, SelectedRecipeId = 0 });
        Assert.Equal(CraftRunHalt.LogUnresponsive, stuck.HaltKind);
    }

    [Fact]
    public void FinishingOrRestarting_ClearsTheHaltKind()
    {
        var run = new CraftRun();
        run.Start(Recipe, 1, Settings);
        run.Halt("stopped by you");

        run.Resume(Settings);
        Assert.Equal(CraftRunHalt.None, run.HaltKind);

        CompleteOneCraft(run, before: 0);
        Tick(run, ReadyInLog(1));
        Assert.False(run.Active);
        Assert.Equal(CraftRunHalt.None, run.HaltKind); // finished, not halted
    }

    [Fact]
    public void WhileTheGameIsTransitioning_NoCommandsAreIssued()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);

        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog() with { Transitioning = true }));
    }

    // ---- gear used up as a material -------------------------------------------------------------------------------

    /// <summary>The log will not start a craft with an "Unselected" row: it is chosen first, then Synthesize.</summary>
    [Fact]
    public void AGearMaterial_IsChosen_BeforeSynthesizeIsPressed()
    {
        var run = new CraftRun();
        run.Start(Recipe, 1, Settings);

        Assert.Equal(CraftRunCommand.ChooseGearMaterial, Tick(run, ReadyInLog() with { GearMaterialUnselected = true })); // opens the picker
        Assert.Equal(CraftRunCommand.ChooseGearMaterial, Tick(run, ReadyInLog() with { GearMaterialUnselected = true })); // takes an entry
        Assert.Equal("Choosing the gear it uses up as a material", run.Status);

        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog()));
    }

    /// <summary>Materia on it, or a gearset's last copy: nothing is clicked, and the reason is what gets reported.</summary>
    [Fact]
    public void AGearMaterial_ThatIsNotSafeToChoose_HaltsWithTheReason_AndClicksNothing()
    {
        var run = new CraftRun();
        run.Start(Recipe, 1, Settings);
        const string why = "the only Steel Crowsbeak Hammer is what a saved gearset wears";

        var command = Tick(run, ReadyInLog() with { GearMaterialUnselected = true, GearMaterialRefusal = why });

        Assert.Equal(CraftRunCommand.None, command);
        Assert.False(run.Active);
        Assert.Equal(CraftRunHalt.MaterialChoice, run.HaltKind);
        Assert.Equal(why, run.HaltReason);
    }

    [Fact]
    public void AGearMaterial_ThePickerWillNotTake_IsGivenUpOn()
    {
        var run = new CraftRun();
        run.Start(Recipe, 1, Settings);

        var asked = 0;
        for (var i = 0; i < 20 && run.Active; i++)
        {
            if (Tick(run, ReadyInLog() with { GearMaterialUnselected = true }) == CraftRunCommand.ChooseGearMaterial)
                asked++;
        }

        Assert.False(run.Active);
        Assert.Equal(CraftRun.MaxRepeats, asked);
        Assert.Equal(CraftRunHalt.LogUnresponsive, run.HaltKind);
        Assert.Contains("could not choose the gear", run.HaltReason);
    }

    [Fact]
    public void CraftCompleted_FiresWithHqFlag_WhenHqArrives()
    {
        var run = new CraftRun();
        run.Start(Recipe, 1, Settings);

        (uint Recipe, bool Hq)? completed = null;
        run.CraftCompleted += (recipe, hq) => completed = (recipe, hq);

        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog(0)));
        Tick(run, Synthesizing(0, 40, 0));
        Tick(run, Synthesizing(100, 10, 0));
        Tick(run, ReadyInLog(0) with { Transitioning = true });
        Tick(run, ReadyInLog(1) with { ResultItemHqCount = 1 });

        Assert.NotNull(completed);
        Assert.Equal(Recipe, completed.Value.Recipe);
        Assert.True(completed.Value.Hq);
    }

    [Fact]
    public void CraftCompleted_FiresWithNqFlag_WhenOnlyNqArrives()
    {
        var run = new CraftRun();
        run.Start(Recipe, 1, Settings);

        (uint Recipe, bool Hq)? completed = null;
        run.CraftCompleted += (recipe, hq) => completed = (recipe, hq);

        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog(0)));
        Tick(run, Synthesizing(0, 40, 0));
        Tick(run, Synthesizing(100, 10, 0));
        Tick(run, ReadyInLog(0) with { Transitioning = true });
        Tick(run, ReadyInLog(1));

        Assert.NotNull(completed);
        Assert.False(completed.Value.Hq);
    }

    // ---- stop after the craft in progress (Hephaestus.RequestStopAfterRun) -----------------------------------------

    /// <summary>A craft is never abandoned: the one on screen is finished and counted, and no next one is begun.</summary>
    [Fact]
    public void StopAfterCraft_FinishesTheSynthesisInProgress_ThenHalts_WithoutStartingAnother()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);
        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(run, ReadyInLog(0)));
        Tick(run, Synthesizing(0, 40));

        run.StopAfterCraft("asked to stop");

        Assert.Equal(CraftRunCommand.None, Tick(run, Synthesizing(100, 10)));
        Assert.True(run.Active);
        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog(0) with { Transitioning = true }));
        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog(1)));
        Assert.Equal(1, run.Completed);
        Assert.True(run.Active); // counted; the halt comes where the next craft would be prepared

        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog(1)));
        Assert.False(run.Active);
        Assert.Equal(CraftRunHalt.Stopped, run.HaltKind);
        Assert.Equal("asked to stop", run.HaltReason);
        Assert.True(run.CanResume); // a person can carry on; nothing does by itself
    }

    [Fact]
    public void StopAfterCraft_BetweenCrafts_HaltsBeforePressingAnything()
    {
        var run = new CraftRun();
        run.Start(Recipe, 3, Settings);
        CompleteOneCraft(run, before: 0);

        run.StopAfterCraft("asked to stop");

        Assert.Equal(CraftRunCommand.None, Tick(run, ReadyInLog(1)));
        Assert.False(run.Active);
        Assert.Equal(CraftRunHalt.Stopped, run.HaltKind);
    }

    /// <summary>No stale request: asked while idle it is dropped, and a fresh start or resume forgets one.</summary>
    [Fact]
    public void StopAfterCraft_WhenIdle_OrBeforeAFreshStart_StopsNothingLater()
    {
        var idle = new CraftRun();
        idle.StopAfterCraft("asked to stop");
        idle.Start(Recipe, 2, Settings);
        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(idle, ReadyInLog()));

        var resumed = new CraftRun();
        resumed.Start(Recipe, 2, Settings);
        resumed.StopAfterCraft("asked to stop");
        Tick(resumed, ReadyInLog());
        Assert.False(resumed.Active);
        resumed.Resume(Settings);
        Assert.Equal(CraftRunCommand.PressSynthesize, Tick(resumed, ReadyInLog()));
    }
}
