using Hephaestus.Features.Crafting;

namespace Hephaestus.Tests.Features.Crafting;

public class GearMaterialTests
{
    private static readonly GearCopy Nq = new(Hq: false, HasMateria: false);
    private static readonly GearCopy Hq = new(Hq: true, HasMateria: false);
    private static readonly GearCopy Melded = new(Hq: false, HasMateria: true);

    private static GearHoldings Hammer(params GearCopy[] offered) => new("Steel Crowsbeak Hammer", 1, offered);

    [Fact]
    public void ASpareCopy_IsChosen()
    {
        var choice = GearMaterial.Choose(Hammer(Nq));

        Assert.True(choice.Safe);
        Assert.True(choice.PreferNq);
    }

    /// <summary>Which copy the game takes is not ours to steer: one melded copy on offer is one too many.</summary>
    [Fact]
    public void ACopyWithMateriaOnOffer_IsNeverRisked_EvenBesideAPlainOne()
    {
        var choice = GearMaterial.Choose(Hammer(Nq, Melded));

        Assert.False(choice.Safe);
        Assert.Contains("materia", choice.Refusal);
    }

    [Fact]
    public void TheLastCopyAGearsetWears_IsNotUsedUp()
    {
        var choice = GearMaterial.Choose(Hammer(Nq) with { GearsetWearsNq = true });

        Assert.False(choice.Safe);
        Assert.Contains("saved gearset", choice.Refusal);
    }

    [Fact]
    public void AGearsetsCopy_WithASpareBesideIt_LeavesOneForTheGearset()
    {
        Assert.True(GearMaterial.Choose(Hammer(Nq, Nq) with { GearsetWearsNq = true }).Safe);
    }

    /// <summary>The gearset's copy is the one being worn: the one in the bags is spare.</summary>
    [Fact]
    public void ACopyBeingWorn_SatisfiesTheGearset()
    {
        Assert.True(GearMaterial.Choose(Hammer(Nq) with { GearsetWearsNq = true, WornNq = 1 }).Safe);
    }

    /// <summary>Each offered quality has to be safe by itself, because either could be the one taken.</summary>
    [Fact]
    public void ASpareInOneQuality_DoesNotExcuseTheGearsetsLastCopyInTheOther()
    {
        var choice = GearMaterial.Choose(Hammer(Nq, Nq, Hq) with { GearsetWearsHq = true });

        Assert.False(choice.Safe);
        Assert.Contains("high-quality", choice.Refusal);
    }

    [Fact]
    public void AGearsetWearingAQualityThatIsNotOnOffer_IsNoObstacle()
    {
        Assert.True(GearMaterial.Choose(Hammer(Nq) with { GearsetWearsHq = true, WornHq = 1 }).Safe);
    }

    [Fact]
    public void NothingOnOffer_IsRefused()
    {
        var choice = GearMaterial.Choose(Hammer() with { WornNq = 1 });

        Assert.False(choice.Safe);
        Assert.Contains("never offered", choice.Refusal);
    }

    [Fact]
    public void MoreThanOnePerCraft_IsLeftToAPerson()
    {
        Assert.False(GearMaterial.Choose(Hammer(Nq, Nq) with { Needed = 2 }).Safe);
    }

    [Fact]
    public void OnlyHqHeld_TakesIt()
    {
        var held = Hammer(Hq);
        var choice = GearMaterial.Choose(held);

        Assert.True(choice.Safe);
        Assert.False(choice.PreferNq);
        Assert.Equal(0, GearMaterial.Entry(choice, held, entryCount: 1));
    }

    /// <summary>Both qualities listed: HQ comes first, so NQ is the last entry.</summary>
    [Fact]
    public void BothQualitiesOffered_TakesTheNqEntry()
    {
        var held = Hammer(Hq, Nq);
        var choice = GearMaterial.Choose(held);

        Assert.Equal(1, GearMaterial.Entry(choice, held, entryCount: 2));
    }

    [Fact]
    public void OneQuality_HoweverManyEntries_TakesTheFirst()
    {
        var held = Hammer(Nq, Nq, Nq);

        Assert.Equal(0, GearMaterial.Entry(GearMaterial.Choose(held), held, entryCount: 3));
    }
}
