using Hephaestus.Features.Crafting.Planner;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;
using Hephaestus.Services;

namespace Hephaestus.Tests.Services;

public sealed class PlannerRunnerTests : IDisposable
{
    private static readonly BaselineRequest Request = new(770, 10040, 21200, 70, false, 100, 314, 301, 5911, 5503, 641, 10600, true, false, false, false);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hephaestus-planner-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static RotationPlan Reaching(params CraftAction[] actions) => new(actions, Completed: true, Reached: true, Quality: 21200);

    [Fact]
    public async Task ARotation_IsPlannedOnce_ThenServedFromTheCache_EvenByAnotherRunner()
    {
        var planned = 0;
        RotationPlan Plan(BaselineRequest request, int threads)
        {
            planned++;
            Assert.Equal(3, threads);
            return Reaching(CraftAction.MuscleMemory, CraftAction.CarefulSynthesis);
        }

        var runner = new PlannerRunner(new BaselineCache(_directory), () => 3, plan: Plan);
        var first = await runner.SolveAsync(Request);
        var second = await runner.SolveAsync(Request);
        var other = await new PlannerRunner(new BaselineCache(_directory), () => 3, plan: Plan).SolveAsync(Request);

        Assert.Equal(new[] { CraftAction.MuscleMemory, CraftAction.CarefulSynthesis }, first);
        Assert.Equal(first, second);
        Assert.Equal(first, other);
        Assert.Equal(1, planned);
        Assert.Contains("cache", runner.Status);
    }

    [Fact]
    public async Task ARotationThatFallsShort_IsStillHandedOver_AndTheStatusSaysSo()
    {
        var runner = new PlannerRunner(new BaselineCache(_directory), () => 1,
            plan: (_, _) => new RotationPlan(new[] { CraftAction.CarefulSynthesis }, Completed: true, Reached: false, Quality: 18000));

        var rotation = await runner.SolveAsync(Request);

        Assert.NotNull(rotation); // what a rotation that falls short MEANS is the session's call, not the runner's
        Assert.Contains("No rotation reaches full quality", runner.Status);
    }

    [Fact]
    public async Task NoFinishedRotation_IsNoRotation_AndIsNotCached()
    {
        var planned = 0;
        var runner = new PlannerRunner(new BaselineCache(_directory), () => 1, plan: (_, _) =>
        {
            planned++;
            return new RotationPlan(Array.Empty<CraftAction>(), Completed: false, Reached: false, Quality: 0);
        });

        Assert.Null(await runner.SolveAsync(Request));
        Assert.Null(await runner.SolveAsync(Request));
        Assert.Equal(2, planned);
    }

    [Fact]
    public async Task APlannerThatThrows_CostsARotation_NotTheCraft()
    {
        var logged = new List<string>();
        var runner = new PlannerRunner(new BaselineCache(_directory), () => 1, logged.Add, (_, _) => throw new InvalidOperationException("out of ideas"));

        Assert.Null(await runner.SolveAsync(Request));
        Assert.Contains("out of ideas", runner.Status);
        Assert.Single(logged);
    }

    [Fact]
    public async Task ADeeperLook_ReplacesTheCachedRotation_OnlyWhenItFoundABetterOne()
    {
        var cache = new BaselineCache(_directory);
        var runner = new PlannerRunner(cache, () => 2,
            plan: (_, _) => Reaching(CraftAction.MuscleMemory, CraftAction.Veneration, CraftAction.CarefulSynthesis),
            deepen: (_, first, threads) =>
            {
                Assert.Equal(2, threads);
                Assert.Equal(3, first.Steps);
                return Reaching(CraftAction.MuscleMemory, CraftAction.CarefulSynthesis);
            });

        var first = await runner.SolveAsync(Request);
        await runner.Deepening;

        Assert.Equal(3, first!.Count); // the craft in hand keeps what it was given
        Assert.Equal(new[] { CraftAction.MuscleMemory, CraftAction.CarefulSynthesis }, cache.Get(OwnPlan.CacheKey(Request)));
        Assert.Contains("next craft", runner.Status);

        // No better: the cache keeps the first answer.
        var other = Request with { MaxCp = 700 };
        var same = new PlannerRunner(cache, () => 1, plan: (_, _) => Reaching(CraftAction.MuscleMemory), deepen: (_, plan, _) => plan);
        await same.SolveAsync(other);
        await same.Deepening;
        Assert.Equal(new[] { CraftAction.MuscleMemory }, cache.Get(OwnPlan.CacheKey(other)));
        Assert.Contains("nothing better", same.Status);
    }

    [Fact]
    public async Task OurRotations_AndRaphaels_LiveSideBySideInOneCache()
    {
        var cache = new BaselineCache(_directory);
        cache.Put(RaphaelCommand.CacheKey(Request), new[] { CraftAction.Reflect });
        var runner = new PlannerRunner(cache, () => 1, plan: (_, _) => Reaching(CraftAction.MuscleMemory));

        Assert.Equal(new[] { CraftAction.MuscleMemory }, await runner.SolveAsync(Request));
        Assert.Equal(new[] { CraftAction.Reflect }, cache.Get(RaphaelCommand.CacheKey(Request)));
    }
}
