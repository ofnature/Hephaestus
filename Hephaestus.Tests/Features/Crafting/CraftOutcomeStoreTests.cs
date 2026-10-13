using System;
using System.IO;
using Hephaestus.Features.Crafting;
using Xunit;

namespace Hephaestus.Tests.Features.Crafting;

public sealed class CraftOutcomeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hephaestus-outcomes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Record_AccumulatesCraftsAndHq()
    {
        var store = new CraftOutcomeStore(_dir);

        store.Record(1, 100, hq: true);
        store.Record(1, 100, hq: false);
        store.Record(1, 100, hq: true);

        var result = store.Lookup(1, 100);
        Assert.NotNull(result);
        Assert.Equal(3, result.Value.Crafts);
        Assert.Equal(2, result.Value.Hq);
    }

    [Fact]
    public void Lookup_UnknownRecipe_IsNull()
    {
        var store = new CraftOutcomeStore(_dir);

        Assert.Null(store.Lookup(1, 999));
    }

    [Fact]
    public void Persists_AcrossInstances()
    {
        new CraftOutcomeStore(_dir).Record(2, 42, hq: true);

        var reloaded = new CraftOutcomeStore(_dir).Lookup(2, 42);
        Assert.NotNull(reloaded);
        Assert.Equal(1, reloaded.Value.Crafts);
        Assert.Equal(1, reloaded.Value.Hq);
    }

    [Fact]
    public void Record_IgnoresMissingCharacterOrRecipe()
    {
        var store = new CraftOutcomeStore(_dir);

        store.Record(0, 100, hq: true); // no character
        store.Record(1, 0, hq: true);   // no recipe

        Assert.Null(store.Lookup(0, 100));
        Assert.Null(store.Lookup(1, 0));
    }
}
