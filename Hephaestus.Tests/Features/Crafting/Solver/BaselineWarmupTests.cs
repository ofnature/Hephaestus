using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Tests.Features.Crafting.Solver;

public class BaselineWarmupTests
{
    private readonly List<(BaselineRequest Request, TaskCompletionSource<IReadOnlyList<CraftAction>?> Done)> _started = new();
    private bool _paused;
    private DateTime _now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private BaselineWarmup Warmup() => new(
        request =>
        {
            var done = new TaskCompletionSource<IReadOnlyList<CraftAction>?>();
            _started.Add((request, done));
            return done.Task;
        },
        () => _paused);

    private static BaselineRequest Request(int recipeLevel) => new(
        RecipeLevel: recipeLevel, MaxProgress: 1000, MaxQuality: 5000, MaxDurability: 70, Expert: false, JobLevel: 90,
        BaseProgress: 200, BaseQuality: 300, Craftsmanship: 3000, Control: 3000, MaxCp: 500, InitialQuality: 0,
        Manipulation: true, HeartAndSoul: false, QuickInnovation: false, BackloadProgress: false);

    private void Tick(BaselineWarmup warmup)
    {
        _now = _now.AddSeconds(1);
        warmup.Tick(_now);
    }

    [Fact]
    public void OneSolveAtATime_InTheOrderQueued()
    {
        var warmup = Warmup();
        warmup.Enqueue(new[] { (Request(1), "Bronze Ingot"), (Request(2), "Maple Lumber"), (Request(3), "Maple Shield") });

        Tick(warmup);
        Tick(warmup);
        Assert.Single(_started);
        Assert.Equal(1, _started[0].Request.RecipeLevel);
        Assert.True(warmup.Solving);
        Assert.Equal("Pre-solving Bronze Ingot (2 more waiting)", warmup.Status);

        _started[0].Done.SetResult(new List<CraftAction> { CraftAction.BasicSynthesis });
        Tick(warmup);
        Assert.Equal(2, _started.Count);
        Assert.Equal(2, _started[1].Request.RecipeLevel);

        _started[1].Done.SetResult(null); // a failed solve does not stop the rest
        Tick(warmup);
        Assert.Equal(3, _started.Count);

        _started[2].Done.SetResult(new List<CraftAction>());
        Tick(warmup);
        Assert.False(warmup.Solving);
        Assert.Equal(0, warmup.Pending);
        Assert.Equal("Idle — the queue's baselines are ready.", warmup.Status);
    }

    /// <summary>A synthesis in progress owns the CPU: nothing new starts until it is over.</summary>
    [Fact]
    public void WhilePaused_NothingStarts_AndItResumesAfter()
    {
        var warmup = Warmup();
        warmup.Enqueue(new[] { (Request(1), "Bronze Ingot") });
        _paused = true;

        Tick(warmup);
        Assert.Empty(_started);
        Assert.StartsWith("Paused (1 waiting)", warmup.Status);

        _paused = false;
        Tick(warmup);
        Assert.Single(_started);
    }

    [Fact]
    public void TheSameRequestTwice_IsQueuedOnce_AndEnqueueSaysWhatWasNew()
    {
        var warmup = Warmup();

        Assert.Equal(2, warmup.Enqueue(new[] { (Request(1), "A"), (Request(2), "B") }));
        Assert.Equal(0, warmup.Enqueue(new[] { (Request(1), "A again") }));
        Assert.Equal(2, warmup.Pending);
    }

    [Fact]
    public void Clear_ForgetsWhatIsWaiting_ButNotTheSolveAlreadyRunning()
    {
        var warmup = Warmup();
        warmup.Enqueue(new[] { (Request(1), "A"), (Request(2), "B") });
        Tick(warmup);

        warmup.Clear();

        Assert.True(warmup.Solving);
        Assert.Equal(0, warmup.Pending);
        _started[0].Done.SetResult(new List<CraftAction>());
        Tick(warmup);
        Assert.Single(_started);
        Assert.Equal(1, warmup.Enqueue(new[] { (Request(1), "A") })); // forgotten, so it may be queued again
    }
}
