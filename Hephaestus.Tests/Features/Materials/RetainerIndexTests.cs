using System.Text.Json;
using Hephaestus.Features.Materials;

namespace Hephaestus.Tests.Features.Materials;

public class RetainerIndexTests
{
    private const uint Ore = 5106;
    private const uint Honey = 4850;
    private readonly DateTime _now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    private static Dictionary<uint, ItemCount> Bags(params (uint Item, int Nq, int Hq)[] stacks)
        => stacks.ToDictionary(s => s.Item, s => new ItemCount(s.Nq, s.Hq));

    [Fact]
    public void WhatWasSeen_IsHeld_AcrossRetainers_MostFirst()
    {
        var index = new RetainerIndex();
        index.Learn(1, "Alpha", Bags((Ore, 40, 5)), _now);
        index.Learn(2, "Beta", Bags((Ore, 99, 0), (Honey, 3, 0)), _now);

        Assert.Equal(144, index.Held(Ore));
        Assert.Equal(3, index.Held(Honey));
        Assert.Equal(0, index.Held(1));
        Assert.Equal(new[] { "Beta", "Alpha" }, index.Holders(Ore).Select(h => h.Retainer.Name));
        Assert.Equal(new ItemCount(40, 5), index.Holders(Ore)[1].Count);
    }

    /// <summary>A retainer seen again is replaced whole: what they no longer hold is gone, not remembered.</summary>
    [Fact]
    public void LearningAgain_ReplacesWhatWasKnown()
    {
        var index = new RetainerIndex();
        index.Learn(1, "Alpha", Bags((Ore, 40, 0), (Honey, 3, 0)), _now);

        index.Learn(1, "Alpha", Bags((Ore, 10, 0)), _now.AddMinutes(5));

        Assert.Equal(10, index.Held(Ore));
        Assert.Equal(0, index.Held(Honey));
        Assert.Equal(_now.AddMinutes(5), index.Retainers[1].LastSeen);
    }

    [Fact]
    public void TheRoster_KnowsRetainers_WhoseBagsWereNeverSeen()
    {
        var index = new RetainerIndex();
        index.Know(1, "Alpha");
        index.Know(2, "Beta");
        index.Learn(1, "Alpha", Bags((Ore, 1, 0)), _now);

        Assert.Equal(new[] { "Beta" }, index.NeverIndexed().Select(r => r.Name));
        Assert.True(index.Retainers[1].Indexed);
    }

    [Fact]
    public void ADismissedRetainer_IsPruned_SoTheirStockIsNeverCountedOn()
    {
        var index = new RetainerIndex();
        index.Learn(1, "Alpha", Bags((Ore, 40, 0)), _now);
        index.Learn(2, "Beta", Bags((Ore, 40, 0)), _now);

        index.Prune(new ulong[] { 2 });

        Assert.Equal(40, index.Held(Ore));
        Assert.False(index.Retainers.ContainsKey(1));
    }

    [Fact]
    public void AWithdrawal_IsTakenOffAtOnce()
    {
        var index = new RetainerIndex();
        index.Learn(1, "Alpha", Bags((Ore, 40, 5)), _now);

        index.Withdrew(1, Ore, nq: 40, hq: 2);
        Assert.Equal(new ItemCount(0, 3), index.Retainers[1].Items[Ore]);

        index.Withdrew(1, Ore, nq: 0, hq: 3);
        Assert.False(index.Retainers[1].Items.ContainsKey(Ore));
    }

    [Fact]
    public void ItSurvivesTheFile()
    {
        var index = new RetainerIndex();
        index.Know(2, "Beta");
        index.Learn(1, "Alpha", Bags((Ore, 40, 5)), _now);

        var json = JsonSerializer.Serialize(index);
        var back = JsonSerializer.Deserialize<RetainerIndex>(json)!;

        Assert.Equal(45, back.Held(Ore));
        Assert.Equal("Alpha", back.Retainers[1].Name);
        Assert.Equal(_now, back.Retainers[1].LastSeen);
        Assert.Equal(new[] { "Beta" }, back.NeverIndexed().Select(r => r.Name));
    }

    [Fact]
    public void TheStore_KeepsOneFilePerCharacter_AndMovesACorruptOneAside()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hephaestus-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RetainerIndexStore(directory);
            var index = new RetainerIndex();
            index.Learn(1, "Alpha", Bags((Ore, 7, 0)), _now);
            store.Save(0xABCD, index);

            Assert.Equal(7, store.Load(0xABCD).Held(Ore));
            Assert.Equal(0, store.Load(0x1234).Held(Ore));

            File.WriteAllText(store.PathFor(0xABCD), "{ not json");
            Assert.Equal(0, store.Load(0xABCD).Held(Ore));
            Assert.True(File.Exists(store.PathFor(0xABCD) + ".corrupt"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
