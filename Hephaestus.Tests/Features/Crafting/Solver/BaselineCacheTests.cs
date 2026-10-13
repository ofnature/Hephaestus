using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Features.Crafting.Solver;

namespace Hephaestus.Tests.Features.Crafting.Solver;

public sealed class BaselineCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HephaestusTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Miss_IsNull()
    {
        Assert.Null(new BaselineCache(_directory).Get("nothing here"));
    }

    [Fact]
    public void PutThenGet_RoundTrips_AcrossInstances()
    {
        new BaselineCache(_directory).Put("key", SolverFixtures.Baseline);

        Assert.Equal(SolverFixtures.Baseline, new BaselineCache(_directory).Get("key"));
    }

    /// <summary>
    /// Several game clients share this file. One client's save must never wipe another's entry —
    /// which is what would happen if each wrote back the copy it loaded at start-up.
    /// </summary>
    [Fact]
    public void TwoClients_AddingDifferentEntries_KeepBoth()
    {
        var first = new BaselineCache(_directory);
        var second = new BaselineCache(_directory);

        first.Put("from the first box", new[] { CraftAction.Reflect });
        second.Put("from the second box", new[] { CraftAction.MuscleMemory });

        Assert.NotNull(first.Get("from the second box"));
        Assert.NotNull(second.Get("from the first box"));
    }

    [Fact]
    public void Put_LeavesNoTempFileBehind()
    {
        new BaselineCache(_directory).Put("key", SolverFixtures.Baseline);

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void ACorruptFile_IsJustAnEmptyCache_AndHealsOnTheNextPut()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "raphael-cache.json"), "{ not json");
        var cache = new BaselineCache(_directory);

        Assert.Null(cache.Get("key"));

        cache.Put("key", new[] { CraftAction.Reflect });
        Assert.Equal(new[] { CraftAction.Reflect }, cache.Get("key"));
    }

    /// <summary>An entry written by a build that knew an action this one does not is a miss, not a crash.</summary>
    [Fact]
    public void AnEntryNamingAnUnknownAction_IsAMiss()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "raphael-cache.json"), "{\"key\":[\"Reflect\",\"SomeFutureAction\"]}");

        Assert.Null(new BaselineCache(_directory).Get("key"));
    }
}
