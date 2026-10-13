using Hephaestus.Features.Materials;

namespace Hephaestus.Tests.Features.Materials;

public class SourcePolicyTests
{
    /// <summary>Holdings first; gathering last — "you can always gather it if you can't buy it" (user, 2026-09-21).</summary>
    [Fact]
    public void Paid_TriesEverySource_HoldingsFirst_GatheringLast()
    {
        Assert.Equal(
            new[]
            {
                MaterialSource.Inventory,
                MaterialSource.FreeCompanyChest,
                MaterialSource.Retainers,
                MaterialSource.SubCraft,
                MaterialSource.VendorPurchase,
                MaterialSource.Gathering,
            },
            SourcePolicy.OrderFor(freeTrial: false));
    }

    /// <summary>Free trial cannot join an FC or hire retainers, so those sources do not exist.</summary>
    [Fact]
    public void FreeTrial_NeverUsesFcChestOrRetainers()
    {
        var order = SourcePolicy.OrderFor(freeTrial: true);

        Assert.DoesNotContain(MaterialSource.FreeCompanyChest, order);
        Assert.DoesNotContain(MaterialSource.Retainers, order);
        Assert.Equal(
            new[] { MaterialSource.Inventory, MaterialSource.SubCraft, MaterialSource.VendorPurchase, MaterialSource.Gathering },
            order);
    }
}
