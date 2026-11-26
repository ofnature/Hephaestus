using Hephaestus.Features.Materials;

namespace Hephaestus.Tests.Features.Materials;

/// <summary>What a special merchant asks for (2026-11-26): the currency a special shop's cost names, and how a cost reads.</summary>
public class SpecialCostTests
{
    private const uint OrdinaryShop = 1769000;

    private static uint Tomestone(uint slot) => slot == 3 ? 46u : 0u; // slot 3 -> some tomestone item

    [Theory]
    [InlineData(10119u, 16, 10119u)] // 8 and above is the item itself, whatever the shop's currency
    [InlineData(8u, 4, 8u)]
    [InlineData(0u, 16, 0u)]
    public void ACostOfEightOrMore_IsTheItemItself(uint cost, byte kind, uint expected)
        => Assert.Equal(expected, SpecialCost.CurrencyItem(cost, kind, OrdinaryShop, Tomestone));

    [Theory]
    [InlineData(1u, 28u)]    // Allagan Tomestone of Poetics
    [InlineData(2u, 33913u)] // Purple Crafters' Scrip
    [InlineData(4u, 33914u)] // Purple Gatherers' Scrip
    [InlineData(6u, 41784u)] // Orange Crafters' Scrip
    [InlineData(7u, 41785u)] // Orange Gatherers' Scrip
    [InlineData(3u, 0u)]     // a slot nobody has named: nothing, not a wrong item
    public void InAScripShop_ASlotIsItsScrip(uint slot, uint expected)
        => Assert.Equal(expected, SpecialCost.CurrencyItem(slot, SpecialCost.ScripCurrency, OrdinaryShop, Tomestone));

    [Fact]
    public void InAGilShop_ASlotIsGil_AndInATomestoneShop_TheSheetsTomestone()
    {
        Assert.Equal(SpecialCost.Gil, SpecialCost.CurrencyItem(1, SpecialCost.GilCurrency, OrdinaryShop, Tomestone));
        Assert.Equal(46u, SpecialCost.CurrencyItem(3, SpecialCost.TomestoneCurrency, OrdinaryShop, Tomestone));
        Assert.Equal(0u, SpecialCost.CurrencyItem(2, SpecialCost.TomestoneCurrency, OrdinaryShop, Tomestone)); // unknown slot
        Assert.Equal(5u, SpecialCost.CurrencyItem(5, useCurrencyType: 2, OrdinaryShop, Tomestone)); // any other kind: as the sheet says
    }

    [Fact]
    public void TheTwoShopsMarkedAsTomestoneShops_ThatSellForScrips_AreReadAsScripShops()
    {
        Assert.Equal(33913u, SpecialCost.CurrencyItem(2, SpecialCost.TomestoneCurrency, shopId: 1770637, Tomestone));
        Assert.Equal(33914u, SpecialCost.CurrencyItem(4, SpecialCost.TomestoneCurrency, shopId: 1770638, Tomestone));
    }

    [Fact]
    public void ACostReads_CountTimesName_GilAsGil_SeveralJoined()
    {
        Assert.Equal("600 × Storm Seal", SpecialCost.Text(new[] { (600u, 20u, "Storm Seal") }));
        Assert.Equal("4,620 gil", SpecialCost.Text(new[] { (4620u, SpecialCost.Gil, "Gil") }));
        Assert.Equal("1 × Duck Broth + 2 × Fire Shard", SpecialCost.Text(new[] { (1u, 100u, "Duck Broth"), (2u, 2u, "Fire Shard") }));
    }

    [Fact]
    public void ACostNobodyCanName_IsLeftOut_AndNothingAtAll_IsEmpty()
    {
        Assert.Equal("50 × Allied Seal", SpecialCost.Text(new[] { (0u, 5u, "Nothing"), (50u, 27u, "Allied Seal"), (3u, 0u, "") }));
        Assert.Equal(string.Empty, SpecialCost.Text(System.Array.Empty<(uint, uint, string)>()));
    }
}
