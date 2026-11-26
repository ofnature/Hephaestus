using System.Numerics;
using Hephaestus.Features.Materials;
using Hephaestus.Services.Game;

namespace Hephaestus.Tests.Features.Materials;

/// <summary>The "Vendor Location" window (2026-11-26): the order of its sellers, how a shop's row is matched, and the table behind sixteen ingredients.</summary>
public class VendorMenuTests
{
    private const uint Limsa = 129;
    private const uint Gridania = 132;
    private const uint Uldah = 130;

    private static PlacedSeller At(uint npc, uint territory, float x = 0f) => new(npc, territory, new Vector3(x, 0f, 0f));

    [Fact]
    public void SellersInTheCharactersZoneComeFirst_NearestFirst_ThenTheRestInSheetOrder()
    {
        var sellers = new[] { At(1, Limsa), At(2, Gridania, x: 300f), At(3, Uldah), At(4, Gridania, x: 12f), At(5, Limsa) };

        var ordered = VendorMenu.Order(sellers, currentTerritory: Gridania, player: new Vector3(10f, 0f, 0f));

        Assert.Equal(new uint[] { 4, 2, 1, 3, 5 }, ordered.Select(s => s.NpcDataId));
    }

    [Fact]
    public void EverySellerIsListed_ThoseNobodyCanPlaceLast()
    {
        var sellers = new[] { At(1, 0), At(2, Limsa), At(3, 0), At(4, Uldah) };

        var ordered = VendorMenu.Order(sellers, currentTerritory: 999, Vector3.Zero);

        Assert.Equal(new uint[] { 2, 4, 1, 3 }, ordered.Select(s => s.NpcDataId));
        Assert.Empty(VendorMenu.Order(System.Array.Empty<PlacedSeller>(), Limsa, Vector3.Zero));
    }

    [Theory]
    [InlineData("Iron Ore", "Iron Ore", true)]
    [InlineData("Iron Ore", "Iron Ingot", false)]
    [InlineData("Wrapped Maple Lon...", "Wrapped Maple Longbow", true)] // a shop cuts a long name short
    [InlineData("Wrapped Maple Lon...", "Wrapped Elm Longbow", false)]
    [InlineData("...", "Iron Ore", false)]
    [InlineData("", "Iron Ore", false)]
    [InlineData("Iron", "Iron Ore", false)] // not cut short: a different, shorter name
    public void AShopRowNamesTheItem_WhenItSaysItsName_OrTheBeginningOfItCutShort(string shown, string item, bool expected)
        => Assert.Equal(expected, VendorMenu.RowNames(shown, item));

    // [SUPERSEDED 2026-11-26 by the two Order tests above — VendorMenu.Pick (one seller a zone, for a submenu) gave way to the window]
    // [Fact]
    // public void OneSellerAZone_TheZoneTheCharacterIsInFirst()
    // {
    //     var sellers = new[] { At(1, Limsa), At(2, Gridania), At(3, Limsa), At(4, Uldah), At(5, Gridania) };
    //
    //     var picked = VendorMenu.Pick(sellers, currentTerritory: Gridania, player: Vector3.Zero);
    //
    //     Assert.Equal(new uint[] { Gridania, Limsa, Uldah }, picked.Select(p => p.Territory));
    //     Assert.Equal(1u, picked[1].NpcDataId); // elsewhere: the first the sheets name
    // }
    //
    // [Fact]
    // public void InTheCharactersZone_TheNearestSeller_NotTheFirstInTheSheet()
    // {
    //     var sellers = new[] { At(1, Limsa, x: 300f), At(2, Limsa, x: 12f), At(3, Limsa, x: -80f) };
    //
    //     var picked = VendorMenu.Pick(sellers, Limsa, new Vector3(10f, 0f, 0f));
    //
    //     Assert.Equal(2u, Assert.Single(picked).NpcDataId);
    // }
    //
    // [Fact]
    // public void NotInAnySellersZone_SheetOrder_AndNothingSold_Nothing()
    // {
    //     var picked = VendorMenu.Pick(new[] { At(1, Limsa), At(2, Uldah) }, currentTerritory: 999, Vector3.Zero);
    //
    //     Assert.Equal(new uint[] { 1, 2 }, picked.Select(p => p.NpcDataId));
    //     Assert.Empty(VendorMenu.Pick(System.Array.Empty<PlacedSeller>(), Limsa, Vector3.Zero));
    // }
    //
    // [Fact]
    // public void AMenuIsShort_TheCharactersZoneIsKept()
    // {
    //     var sellers = Enumerable.Range(1, 30).Select(i => At((uint)i, (uint)(1000 + i))).Append(At(99, Limsa)).ToArray();
    //
    //     var picked = VendorMenu.Pick(sellers, Limsa, Vector3.Zero);
    //
    //     Assert.Equal(VendorMenu.MaxEntries, picked.Count);
    //     Assert.Equal(99u, picked[0].NpcDataId);
    // }

    [Fact]
    public void TheTableOfSellersReadOutOfProcess_NamesEachNpcOnce_InTheStartingCitiesAndTheShroud()
    {
        var table = VendorLocations.ReadOutOfProcess;

        Assert.Equal(table.Length, table.Select(row => row.Npc).Distinct().Count());
        Assert.All(table, row => Assert.Contains(row.Territory, new uint[] { 128, 129, 132, 133, 148, 152, 153, 154 }));
        // The two pairs Item Vendor Location's list has in each other's city: the game data says otherwise.
        Assert.Equal(133u, table.Single(row => row.Npc == 1006004).Territory);
        Assert.Equal(128u, table.Single(row => row.Npc == 1006005).Territory);
    }
}
