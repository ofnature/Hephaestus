using System.Linq;
using Hephaestus.Features.Workshop;
using Xunit;

namespace Hephaestus.Tests.Features.Workshop;

public sealed class WorkshopQueueTests
{
    private static WorkshopBuild Build() => new(10160, "Odyssey-type Hull", new[]
    {
        new WorkshopPhase(1, new[] { new WorkshopMaterial(5380, "Rosewood Lumber", 24), new WorkshopMaterial(999, "Unaspected Crystal", 3) }),
        new WorkshopPhase(2, new[] { new WorkshopMaterial(5381, "Dark Chestnut Lumber", 12) }),
        new WorkshopPhase(3, new[] { new WorkshopMaterial(5382, "Varnish", 24) }),
    });

    [Fact]
    public void Plan_GroupsLinesByPhase_InOrder()
    {
        var phases = WorkshopQueue.Plan(Build(), itemId => itemId switch
        {
            5380 => 1000,
            5381 => 1001,
            5382 => 1002,
            _ => null,
        });

        Assert.Equal(new[] { 1, 2, 3 }, phases.Select(p => p.Number));
        Assert.Equal(1000u, phases[0].Lines[0].RecipeId);
        Assert.Equal(1002u, phases[2].Lines[0].RecipeId);
    }

    [Fact]
    public void Plan_MarksNonCraftableMaterial_WithNullRecipe()
    {
        var phases = WorkshopQueue.Plan(Build(), _ => null);

        Assert.Null(phases[0].Lines[1].RecipeId); // Unaspected Crystal
    }

    [Fact]
    public void Flatten_SkipsNonCraftable_AndPreservesPhaseOrder()
    {
        var phases = WorkshopQueue.Plan(Build(), itemId => itemId switch
        {
            5380 => 1000,
            5381 => 1001,
            5382 => 1002,
            _ => null,
        });

        var flat = WorkshopQueue.Flatten(phases).ToList();
        Assert.Equal(new[] { (1000u, 24), (1001u, 12), (1002u, 24) }, flat);
    }
}
