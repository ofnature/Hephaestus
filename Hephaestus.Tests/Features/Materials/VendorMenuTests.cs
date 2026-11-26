using System.Numerics;
using Hephaestus.Features.Materials;
using Hephaestus.Services.Game;

namespace Hephaestus.Tests.Features.Materials;

/// <summary>The "Vendor Location" right-click menu (2026-11-26): which sellers it offers, and the table behind sixteen ingredients.</summary>
public class VendorMenuTests
{
    private const uint Limsa = 129;
    private const uint Gridania = 132;
    private const uint Uldah = 130;

    private static PlacedSeller At(uint npc, uint territory, float x = 0f) => new(npc, territory, new Vector3(x, 0f, 0f));

    [Fact]
    public void OneSellerAZone_TheZoneTheCharacterIsInFirst()
    {
        var sellers = new[] { At(1, Limsa), At(2, Gridania), At(3, Limsa), At(4, Uldah), At(5, Gridania) };

        var picked = VendorMenu.Pick(sellers, currentTerritory: Gridania, player: Vector3.Zero);

        Assert.Equal(new uint[] { Gridania, Limsa, Uldah }, picked.Select(p => p.Territory));
        Assert.Equal(1u, picked[1].NpcDataId); // elsewhere: the first the sheets name
    }

    [Fact]
    public void InTheCharactersZone_TheNearestSeller_NotTheFirstInTheSheet()
    {
        var sellers = new[] { At(1, Limsa, x: 300f), At(2, Limsa, x: 12f), At(3, Limsa, x: -80f) };

        var picked = VendorMenu.Pick(sellers, Limsa, new Vector3(10f, 0f, 0f));

        Assert.Equal(2u, Assert.Single(picked).NpcDataId);
    }

    [Fact]
    public void NotInAnySellersZone_SheetOrder_AndNothingSold_Nothing()
    {
        var picked = VendorMenu.Pick(new[] { At(1, Limsa), At(2, Uldah) }, currentTerritory: 999, Vector3.Zero);

        Assert.Equal(new uint[] { 1, 2 }, picked.Select(p => p.NpcDataId));
        Assert.Empty(VendorMenu.Pick(System.Array.Empty<PlacedSeller>(), Limsa, Vector3.Zero));
    }

    [Fact]
    public void AMenuIsShort_TheCharactersZoneIsKept()
    {
        var sellers = Enumerable.Range(1, 30).Select(i => At((uint)i, (uint)(1000 + i))).Append(At(99, Limsa)).ToArray();

        var picked = VendorMenu.Pick(sellers, Limsa, Vector3.Zero);

        Assert.Equal(VendorMenu.MaxEntries, picked.Count);
        Assert.Equal(99u, picked[0].NpcDataId);
    }

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
