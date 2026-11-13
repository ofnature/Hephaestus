using Hephaestus.Features.Crafting;

namespace Hephaestus.Tests.Features.Crafting;

public class CraftRunLauncherTests
{
    private static readonly CraftRunSettings Settings = new(MaxCraftRetries: 2, GearConditionThresholdPercent: 20);

    private static (CraftRun Run, CraftRunLauncher Launcher) Create()
    {
        var run = new CraftRun();
        return (run, new CraftRunLauncher(run, () => Settings));
    }

    [Fact]
    public void CountZero_MeansCraftAll_LabelledWithTheCraftableCount()
    {
        var (_, launcher) = Create();

        Assert.Equal("Craft All (7)", launcher.Label(craftable: 7));
        Assert.Equal(7, launcher.Target(craftable: 7));
    }

    [Fact]
    public void ACount_OverridesCraftAll()
    {
        var (_, launcher) = Create();
        launcher.Count = 3;

        Assert.Equal("Craft 3 (max 7)", launcher.Label(craftable: 7));
        Assert.Equal(3, launcher.Target(craftable: 7));
    }

    /// <summary>A count is a wish; the materials and crystals on hand are the limit. The label says so.</summary>
    [Fact]
    public void ACountBeyondWhatCanBeMade_IsCutDownToTheMax()
    {
        var (run, launcher) = Create();
        launcher.Count = 5;

        Assert.Equal("Craft 3 (max 3)", launcher.Label(craftable: 3));
        launcher.Start(selectedRecipeId: 5, craftable: 3);

        Assert.Equal(3, run.Requested);
    }

    /// <summary>Seen in game: count 1, nothing craftable, and the button still offered "Craft 1".</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)] // unreadable
    public void ACount_WithNothingCraftable_CannotStart(int craftable)
    {
        var (run, launcher) = Create();
        launcher.Count = 1;

        Assert.Equal("Craft 0 (max 0)", launcher.Label(craftable));
        Assert.False(launcher.CanStart(selectedRecipeId: 5, craftable));
        launcher.Start(selectedRecipeId: 5, craftable);
        Assert.False(run.Active);
    }

    /// <summary>The log reports -1 while its count is unreadable — that must never become a negative run.</summary>
    [Fact]
    public void UnreadableCraftable_IsTreatedAsZero()
    {
        var (_, launcher) = Create();

        Assert.Equal("Craft All (0)", launcher.Label(craftable: -1));
        Assert.False(launcher.CanStart(selectedRecipeId: 5, craftable: -1));
    }

    [Fact]
    public void Start_RunsTheSelectedRecipeForTheTarget()
    {
        var (run, launcher) = Create();

        launcher.Start(selectedRecipeId: 5, craftable: 4);

        Assert.True(run.Active);
        Assert.Equal(5u, run.RecipeId);
        Assert.Equal(4, run.Requested);
    }

    [Theory]
    [InlineData(0u, 4)] // no recipe selected
    [InlineData(5u, 0)] // nothing craftable
    public void Start_WithNothingToDo_DoesNothing(uint recipeId, int craftable)
    {
        var (run, launcher) = Create();

        launcher.Start(recipeId, craftable);

        Assert.False(run.Active);
    }

    /// <summary>While the queue is sequencing runs, a manual Craft All would fight it for the character.</summary>
    [Fact]
    public void WhileTheQueueHasTheCharacter_ManualStartsAndResumesAreOff()
    {
        var run = new CraftRun();
        var queueActive = true;
        var launcher = new CraftRunLauncher(run, () => Settings, blocked: () => queueActive);

        Assert.False(launcher.CanStart(5, 4));
        launcher.Start(5, 4);
        Assert.False(run.Active);

        queueActive = false;
        launcher.Start(5, 4);
        Assert.True(run.Active);

        run.Halt("stopped by you");
        queueActive = true;
        launcher.Resume();
        Assert.False(run.Active);
    }

    /// <summary>
    /// Craft All on a recipe for ANOTHER job: the launcher must not start the run (Synthesize would be
    /// refused on the wrong job) — it hands recipe and count to whoever can change job first.
    /// </summary>
    [Fact]
    public void AStartAnotherPluginAskedFor_MakesTheAmountAsked_NeverMoreThanTheMaterialsMake_AndLeavesTheCountBoxAlone()
    {
        var (run, launcher) = Create();
        launcher.Count = 7; // the player's own box

        Assert.True(launcher.StartRequested(selectedRecipeId: 1234, craftable: 10, amount: 3));
        Assert.True(run.Active);
        Assert.Equal((1234u, 3), (run.RecipeId, run.Requested));
        Assert.Equal(7, launcher.Count);

        var (capped, second) = Create();
        Assert.True(second.StartRequested(1234, craftable: 2, amount: 999));
        Assert.Equal(2, capped.Requested);
    }

    [Theory]
    [InlineData(0u, 10, 3)]   // nothing selected
    [InlineData(1234u, 0, 3)] // nothing can be made
    [InlineData(1234u, -1, 3)] // the log has not said yet
    [InlineData(1234u, 10, 0)] // nothing asked for
    public void AStartAnotherPluginAskedFor_WithNothingToDo_IsRefused(uint recipeId, int craftable, int amount)
    {
        var (run, launcher) = Create();

        Assert.False(launcher.StartRequested(recipeId, craftable, amount));
        Assert.False(run.Active);
    }

    [Fact]
    public void AStartAnotherPluginAskedFor_IsRefusedWhileSomethingRuns_ButReplacesARunLeftHalted()
    {
        var queueActive = true;
        var run = new CraftRun();
        var launcher = new CraftRunLauncher(run, () => Settings, blocked: () => queueActive);
        Assert.False(launcher.StartRequested(1234, 10, 1)); // the queue has the character

        queueActive = false;
        Assert.True(launcher.StartRequested(1234, 10, 5));
        Assert.False(launcher.StartRequested(1234, 10, 1)); // a run is under way

        run.Halt("the craft would not start");
        Assert.True(run.CanResume);
        Assert.True(launcher.StartRequested(4321, 10, 2)); // a pause nobody will resume does not block a caller
        Assert.Equal((4321u, 2, 0), (run.RecipeId, run.Requested, run.Completed));
    }

    [Fact]
    public void ARecipeForAnotherJob_IsHandedOver_NotStartedHere()
    {
        var run = new CraftRun();
        (uint Recipe, int Count)? handedOver = null;
        var launcher = new CraftRunLauncher(run, () => Settings, viaJobChange: (recipe, count) =>
        {
            handedOver = (recipe, count);
            return true;
        });
        launcher.Count = 3;

        launcher.Start(selectedRecipeId: 5, craftable: 7);

        Assert.Equal((5u, 3), handedOver);
        Assert.False(run.Active);
    }

    [Fact]
    public void ARecipeForTheCurrentJob_IsDeclinedByTheJobChanger_AndStartsHere()
    {
        var run = new CraftRun();
        var launcher = new CraftRunLauncher(run, () => Settings, viaJobChange: (_, _) => false);

        launcher.Start(selectedRecipeId: 5, craftable: 4);

        Assert.True(run.Active);
        Assert.Equal(4, run.Requested);
    }

    /// <summary>The same gate as a direct start: no recipe, nothing to make, the queue busy, a run waiting to resume.</summary>
    [Fact]
    public void AStartThatIsNotAllowed_IsNeverOfferedToTheJobChanger()
    {
        var offered = 0;
        var queueActive = true;
        var launcher = new CraftRunLauncher(new CraftRun(), () => Settings, blocked: () => queueActive, viaJobChange: (_, _) =>
        {
            offered++;
            return true;
        });

        launcher.Start(5, 4); // the queue has the character
        queueActive = false;
        launcher.Start(0, 4); // no recipe selected
        launcher.Start(5, 0); // nothing craftable, no count typed

        Assert.Equal(0, offered);
    }

    /// <summary>A halted run must be resumed or cleared — a second click must not silently replace it.</summary>
    [Fact]
    public void AHaltedRun_BlocksAFreshStart_UntilResumedOrCleared()
    {
        var (run, launcher) = Create();
        launcher.Start(5, 4);
        run.Halt("stopped by you");

        Assert.False(launcher.CanStart(5, 4));

        launcher.Resume();
        Assert.True(run.Active);
        Assert.Equal(4, run.Requested);
    }
}
