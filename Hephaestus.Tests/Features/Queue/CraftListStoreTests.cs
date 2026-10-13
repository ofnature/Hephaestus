using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

public sealed class CraftListStoreTests : IDisposable
{
    private const ulong Main = 0x0040_0000_1111_2222;
    private const ulong Alt = 0x0040_0000_3333_4444;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HephaestusTests_" + Guid.NewGuid().ToString("N"));
    private readonly CraftListStore _store;

    public CraftListStoreTests()
    {
        _store = new CraftListStore(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Load_NoFile_IsEmptyQueue()
    {
        Assert.Empty(_store.Load(Main).Items);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsItemsAndProgress()
    {
        var list = new CraftList
        {
            Items =
            {
                new CraftListItem { RecipeId = 35499, Quantity = 12, Crafted = 4 },
                new CraftListItem { RecipeId = 5, Quantity = 1 },
            },
        };

        _store.Save(Main, list);
        var loaded = _store.Load(Main);

        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal(35499u, loaded.Items[0].RecipeId);
        Assert.Equal(12, loaded.Items[0].Quantity);
        Assert.Equal(4, loaded.Items[0].Crafted);
        Assert.Equal(35499u, loaded.NextPending()!.RecipeId);
    }

    /// <summary>
    /// The reason the queue is not in the shared plugin config: every box writes only its own file,
    /// so two clients saving at once cannot overwrite each other's progress.
    /// </summary>
    [Fact]
    public void Characters_AreIsolatedFromEachOther()
    {
        _store.Save(Main, new CraftList { Items = { new CraftListItem { RecipeId = 1, Quantity = 1 } } });
        _store.Save(Alt, new CraftList { Items = { new CraftListItem { RecipeId = 2, Quantity = 9 } } });

        Assert.NotEqual(_store.PathFor(Main), _store.PathFor(Alt));
        Assert.Equal(1u, _store.Load(Main).Items.Single().RecipeId);
        Assert.Equal(2u, _store.Load(Alt).Items.Single().RecipeId);
    }

    [Fact]
    public void Save_LeavesNoTempFileBehind()
    {
        _store.Save(Main, new CraftList());

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_store.PathFor(Main))!, "*.tmp"));
    }

    /// <summary>A queue is never silently destroyed: an unreadable file is kept for inspection.</summary>
    [Fact]
    public void Load_CorruptFile_IsSetAsideNotOverwritten()
    {
        _store.Save(Main, new CraftList());
        File.WriteAllText(_store.PathFor(Main), "{ this is not json");

        var loaded = _store.Load(Main);

        Assert.Empty(loaded.Items);
        Assert.True(File.Exists(_store.PathFor(Main) + ".corrupt"));
        Assert.False(File.Exists(_store.PathFor(Main)));
    }

    /// <summary>A flag is a request for review — it has to survive a restart, or the review never happens.</summary>
    [Fact]
    public void SkipFlagsAndKeepOrder_SurviveARoundTrip()
    {
        var list = new CraftList { KeepOrder = true, Items = { new CraftListItem { RecipeId = 1, Quantity = 2, SkipReason = "out of materials" } } };

        _store.Save(Main, list);
        var loaded = _store.Load(Main);

        Assert.True(loaded.KeepOrder);
        Assert.Equal("out of materials", loaded.Items[0].SkipReason);
        Assert.False(loaded.Items[0].IsRunnable);
        Assert.DoesNotContain("IsRunnable", File.ReadAllText(_store.PathFor(Main)));
    }

    /// <summary>Computed properties must not leak into the saved format.</summary>
    [Fact]
    public void SavedFile_ContainsOnlyStoredFields()
    {
        _store.Save(Main, new CraftList { Items = { new CraftListItem { RecipeId = 1, Quantity = 2 } } });

        var json = File.ReadAllText(_store.PathFor(Main));

        Assert.DoesNotContain("TotalQuantity", json);
        Assert.DoesNotContain("TotalCrafted", json);
        Assert.DoesNotContain("IsComplete", json);
        Assert.Contains("FormatVersion", json);
    }
}
