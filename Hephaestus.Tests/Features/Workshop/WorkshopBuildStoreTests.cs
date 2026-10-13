using System;
using System.IO;
using Hephaestus.Features.Workshop;
using Xunit;

namespace Hephaestus.Tests.Features.Workshop;

public sealed class WorkshopBuildStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hephaestus-workshop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Add_PersistsAcrossInstances()
    {
        new WorkshopBuildStore(_dir).Add(1, 10160);

        var reloaded = new WorkshopBuildStore(_dir).Load(1);
        Assert.Single(reloaded);
        Assert.Equal(10160u, reloaded[0].PartItemId);
    }

    [Fact]
    public void Add_IgnoresDuplicate()
    {
        var store = new WorkshopBuildStore(_dir);
        store.Add(1, 10160);
        store.Add(1, 10160);

        Assert.Single(store.Load(1));
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var store = new WorkshopBuildStore(_dir);
        store.Add(1, 10160);
        store.Add(1, 10161);
        store.Remove(1, 10160);

        var list = store.Load(1);
        Assert.Single(list);
        Assert.Equal(10161u, list[0].PartItemId);
    }

    [Fact]
    public void IgnoresMissingCharacterOrPart()
    {
        var store = new WorkshopBuildStore(_dir);
        store.Add(0, 10160);
        store.Add(1, 0);

        Assert.Empty(store.Load(0));
        Assert.Empty(store.Load(1));
    }
}
