using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Tests.Features.Crafting.Driving;

public class CarpenterActionIdsTests
{
    /// <summary>
    /// An action the simulator can recommend but the executor cannot press would stall a craft
    /// (pressed three times, masked, re-evaluated). Adding an action to the enum must add its id.
    /// </summary>
    [Fact]
    public void EveryAction_HasAnId()
    {
        foreach (var action in Enum.GetValues<CraftAction>())
            Assert.True(CarpenterActionIds.All.ContainsKey(action), $"{action} has no Carpenter action id");
    }

    [Fact]
    public void Ids_AreDistinct()
    {
        Assert.Equal(CarpenterActionIds.All.Count, CarpenterActionIds.All.Values.Distinct().Count());
    }

    /// <summary>The ids raphael-cli itself printed for a Carpenter rotation (its action_ids output variable).</summary>
    [Theory]
    [InlineData(CraftAction.MuscleMemory, 100379u)]
    [InlineData(CraftAction.Manipulation, 4574u)]
    [InlineData(CraftAction.Veneration, 19297u)]
    [InlineData(CraftAction.WasteNot2, 4639u)]
    [InlineData(CraftAction.Groundwork, 100403u)]
    [InlineData(CraftAction.BasicTouch, 100002u)]
    [InlineData(CraftAction.RefinedTouch, 100443u)]
    [InlineData(CraftAction.DelicateSynthesis, 100323u)]
    [InlineData(CraftAction.Innovation, 19004u)]
    [InlineData(CraftAction.PreparatoryTouch, 100299u)]
    [InlineData(CraftAction.StandardTouch, 100004u)]
    [InlineData(CraftAction.AdvancedTouch, 100411u)]
    [InlineData(CraftAction.TrainedFinesse, 100435u)]
    [InlineData(CraftAction.GreatStrides, 260u)]
    [InlineData(CraftAction.ByregotsBlessing, 100339u)]
    [InlineData(CraftAction.BasicSynthesis, 100001u)]
    public void Ids_AgreeWithWhatRaphaelCliPrints(CraftAction action, uint expected)
    {
        Assert.Equal(expected, CarpenterActionIds.All[action]);
    }
}
