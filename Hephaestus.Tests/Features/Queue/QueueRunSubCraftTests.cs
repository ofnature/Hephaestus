using Hephaestus.Features.Crafting;
using Hephaestus.Features.Materials;
using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

/// <summary>The queue engine with a material planner: intermediates first, precise flags, never a loop.</summary>
public class QueueRunSubCraftTests
{
    private const uint Carpenter = 8;
    private const uint Blacksmith = 9;

    private const uint Ore = 1;
    private const uint Shard = 2;
    private const uint Ingot = 100;
    private const uint Lumber = 101;

    private const uint IngotRecipe = 20;
    private const uint ShieldRecipe = 24;

    private static readonly RecipeInfo[] AllRecipes =
    {
        new(IngotRecipe, Blacksmith, "Bronze Ingot", Ingot, new RecipeInput[] { new(Ore, 3), new(Shard, 1) }),
        new(ShieldRecipe, Carpenter, "Maple Shield", 104, new RecipeInput[] { new(Ingot, 2), new(Lumber, 1) }),
    };

    private readonly Dictionary<uint, int> _held = new();
    private DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
    private bool _expand = true;

    private static RecipeInfo? Info(uint id) => AllRecipes.Where(r => r.RecipeId == id).Select(r => (RecipeInfo?)r).FirstOrDefault();

    private QueueRun Queue()
    {
        var planner = new MaterialPlanner(
            Info,
            item => AllRecipes.Where(r => r.ResultItemId == item).Select(r => new RecipeOption(r.RecipeId, r.JobId)).ToList(),
            _ => true,
            item => _held.GetValueOrDefault(item),
            item => item == Ore ? "Copper Ore" : item == Ingot ? "Bronze Ingot" : $"item {item}");
        return new QueueRun(Info, _ => true, planner, () => _expand);
    }

    private static CraftList Shields(int quantity)
    {
        var list = new CraftList();
        list.Items.Add(new CraftListItem { RecipeId = ShieldRecipe, Quantity = quantity });
        return list;
    }

    private static QueueObservation Obs(uint job, bool runActive = false, int runCompleted = 0, CraftRunHalt halt = CraftRunHalt.None, string runStatus = "")
        => new(job, CanSwitchJob: true, LogOpen: false, runActive, runCompleted, halt, runStatus);

    private QueueCommand Tick(QueueRun queue, QueueObservation obs)
    {
        _now = _now.AddSeconds(2);
        return queue.Tick(obs, _now);
    }

    /// <summary>Tick until the engine asks for a craft, following any job change it asks for on the way.</summary>
    private QueueCommand TickToStart(QueueRun queue, ref uint job)
    {
        for (var i = 0; i < 10; i++)
        {
            var command = Tick(queue, Obs(job));
            if (command.Kind == QueueCommandKind.SwitchJob)
                job = command.JobId;
            else if (command.Kind == QueueCommandKind.StartCraft || !queue.Active)
                return command;
        }

        throw new InvalidOperationException("the queue never started a craft");
    }

    /// <summary>A run that makes <paramref name="made"/> and ends the way <paramref name="halt"/> says.</summary>
    private void RunEnds(QueueRun queue, uint job, int made, CraftRunHalt halt = CraftRunHalt.None, string status = "")
    {
        Tick(queue, Obs(job, runActive: true));
        Tick(queue, Obs(job, runActive: false, runCompleted: made, halt: halt, runStatus: status));
    }

    [Fact]
    public void TheIngotsAreMadeFirst_OnTheirOwnJob_ThenTheShields()
    {
        _held[Ingot] = 2; // three shields want six
        _held[Ore] = 12;
        _held[Shard] = 4;
        _held[Lumber] = 3;
        var queue = Queue();
        var list = Shields(3);
        queue.Start(list, null);
        var job = Carpenter;

        var first = TickToStart(queue, ref job);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: IngotRecipe, Count: 4), first);
        Assert.Equal(Blacksmith, job);
        Assert.Equal(IngotRecipe, queue.CurrentSubCraft?.Recipe.RecipeId);
        Assert.Null(queue.CurrentItem);
        Assert.Contains("Bronze Ingot first", queue.Status);
        Assert.Contains("Maple Shield", queue.Status);

        (_held[Ingot], _held[Ore], _held[Shard]) = (6, 0, 0);
        RunEnds(queue, job, made: 4);

        Assert.Equal(0, list.Items[0].Crafted); // four ingots are not four shields

        var second = TickToStart(queue, ref job);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: ShieldRecipe, Count: 3), second);
        Assert.Equal(Carpenter, job);
        Assert.Null(queue.CurrentSubCraft);
        Assert.Same(list.Items[0], queue.CurrentItem);
    }

    [Fact]
    public void AnEntryThatCannotBeSupplied_IsFlaggedWithWhatItIsShortOf()
    {
        _held[Lumber] = 3;
        _held[Shard] = 50; // no ore, so no ingots
        var queue = Queue();
        var list = Shields(3);
        queue.Start(list, null);
        var job = Carpenter;

        TickToStart(queue, ref job);

        Assert.False(queue.Active);
        Assert.Equal("short of 18 × Copper Ore", list.Items[0].SkipReason);
        Assert.Equal(1, queue.ItemsSkipped);
    }

    [Fact]
    public void WithMaterialsForSome_ItMakesThose_ThenFlagsTheRest()
    {
        _held[Ingot] = 4; // two shields' worth, and nothing to smelt more from
        _held[Lumber] = 3;
        var queue = Queue();
        var list = Shields(3);
        queue.Start(list, null);
        var job = Carpenter;

        var start = TickToStart(queue, ref job);
        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: ShieldRecipe, Count: 2), start);

        Tick(queue, Obs(job, runActive: true));
        (_held[Ingot], _held[Lumber]) = (0, 1);
        Tick(queue, Obs(job, runActive: true, runCompleted: 2));
        Tick(queue, Obs(job, runActive: false, runCompleted: 2));
        TickToStart(queue, ref job);

        Assert.Equal(2, list.Items[0].Crafted);
        Assert.StartsWith("short of 6 × Copper Ore", list.Items[0].SkipReason);
        Assert.False(queue.Active);
    }

    [Theory]
    [InlineData(CraftRunHalt.FailedRepeatedly, "the synthesis kept failing")]
    [InlineData(CraftRunHalt.WouldNotStart, "would not start")]
    [InlineData(CraftRunHalt.LogUnresponsive, "would not open the recipe")]
    public void ASubCraftThatWillNotRun_IsNotAskedForAgain_AndTheEntrySaysWhy(CraftRunHalt halt, string expected)
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        _held[Lumber] = 3;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);
        var job = Carpenter;
        Assert.Equal(IngotRecipe, TickToStart(queue, ref job).RecipeId);

        Tick(queue, Obs(job, runActive: true));
        var cleanup = Tick(queue, Obs(job, runActive: false, halt: halt));
        Assert.Equal(QueueCommandKind.ClearRun, cleanup.Kind); // the halted run must not linger as "resumable"

        TickToStart(queue, ref job);

        Assert.False(queue.Active);
        Assert.StartsWith("short of 2 × Bronze Ingot (could not be made: ", list.Items[0].SkipReason);
        Assert.Contains(expected, list.Items[0].SkipReason);
    }

    /// <summary>We counted the bags and thought an ingot could be made; the game said no. Asking again would be a loop.</summary>
    [Fact]
    public void ASubCraftTheGameSaysHasNoMaterials_IsNotALoop()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        _held[Lumber] = 3;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);
        var job = Carpenter;
        TickToStart(queue, ref job);

        RunEnds(queue, job, made: 0, halt: CraftRunHalt.OutOfMaterials);
        TickToStart(queue, ref job);

        Assert.False(queue.Active);
        Assert.Contains("the game says the materials are not there", list.Items[0].SkipReason);
    }

    [Fact]
    public void ASubCraftThatRanShort_IsSimplyReplanned()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        _held[Lumber] = 3;
        var queue = Queue();
        queue.Start(Shields(2), null);
        var job = Carpenter;
        Assert.Equal(4, TickToStart(queue, ref job).Count);

        _held[Ingot] = 3; // three landed, then the run stopped short
        RunEnds(queue, job, made: 3, halt: CraftRunHalt.OutOfMaterials);

        var again = TickToStart(queue, ref job);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: IngotRecipe, Count: 1), again);
    }

    [Fact]
    public void SomethingThatNeedsAPerson_PausesTheQueue_DuringASubCraftToo_AndResumeReplans()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        _held[Lumber] = 3;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);
        var job = Carpenter;
        TickToStart(queue, ref job);

        RunEnds(queue, job, made: 1, halt: CraftRunHalt.GearGate, status: "gear is at 12% — repair, then resume");

        Assert.False(queue.Active);
        Assert.True(queue.CanResume);
        Assert.Contains("Paused at Bronze Ingot", queue.Status);
        Assert.Null(list.Items[0].SkipReason);

        _held[Ingot] = 1;
        queue.Resume();
        var resumed = TickToStart(queue, ref job);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: IngotRecipe, Count: 1), resumed);
    }

    /// <summary>A block lasts until a person has had the chance to fix what was wrong.</summary>
    [Fact]
    public void Retry_ThenStart_GivesABlockedSubCraftAnotherGo()
    {
        _held[Ore] = 50;
        _held[Shard] = 50;
        _held[Lumber] = 3;
        var queue = Queue();
        var list = Shields(1);
        queue.Start(list, null);
        var job = Carpenter;
        TickToStart(queue, ref job);
        RunEnds(queue, job, made: 0, halt: CraftRunHalt.WouldNotStart);
        TickToStart(queue, ref job);
        Assert.NotNull(list.Items[0].SkipReason);

        list.Items[0].SkipReason = null; // the player's Retry button
        queue.Start(list, null);

        Assert.Equal(IngotRecipe, TickToStart(queue, ref job).RecipeId);
    }

    [Fact]
    public void SwitchedOff_EntriesAreCraftedExactlyAsListed()
    {
        _expand = false;
        var queue = Queue();
        queue.Start(Shields(3), null);
        var job = Carpenter;

        var start = TickToStart(queue, ref job);

        Assert.Equal(new QueueCommand(QueueCommandKind.StartCraft, RecipeId: ShieldRecipe, Count: 3), start);
    }
}
