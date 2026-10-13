using Hephaestus.Features.Crafting.Driving;
using Hephaestus.Features.Crafting.Simulation;
using Kind = Hephaestus.Features.Crafting.Driving.HotbarCommandKind;

namespace Hephaestus.Tests.Features.Crafting.Driving;

public class HotbarMatcherTests
{
    private const uint BasicTouch = 100002;   // a CraftAction-sheet action
    private const uint Manipulation = 4574;   // an Action-sheet action
    private const uint HastyTouch = 100355;
    private const uint DaringTouch = 100451;

    [Fact]
    public void KindFollowsTheIdRange()
    {
        Assert.Equal(Kind.CraftAction, HotbarMatcher.KindOf(BasicTouch));
        Assert.Equal(Kind.Action, HotbarMatcher.KindOf(Manipulation));
    }

    [Fact]
    public void ASlotHoldingTheAction_Matches()
    {
        Assert.True(HotbarMatcher.Matches(Kind.CraftAction, BasicTouch, Kind.CraftAction, BasicTouch, BasicTouch));
        Assert.True(HotbarMatcher.Matches(Kind.Action, Manipulation, Kind.Action, Manipulation, Manipulation));
    }

    [Fact]
    public void ASlotHoldingSomethingElse_DoesNot()
    {
        Assert.False(HotbarMatcher.Matches(Kind.CraftAction, 100001, Kind.CraftAction, 100001, BasicTouch));
        Assert.False(HotbarMatcher.Matches(Kind.Other, 0, Kind.Other, 0, BasicTouch));
    }

    /// <summary>Macro #4574 or item #4574 is not Manipulation. The kind has to agree, not just the number.</summary>
    [Theory]
    [InlineData(Kind.Other)]
    [InlineData(Kind.CraftAction)]
    public void TheSameNumber_OfTheWrongKind_DoesNotMatch(Kind wrongKind)
    {
        Assert.False(HotbarMatcher.Matches(wrongKind, Manipulation, wrongKind, Manipulation, Manipulation));
    }

    [Fact]
    public void AnUnknownAction_NeverMatchesAnything()
    {
        Assert.False(HotbarMatcher.Matches(Kind.Other, 0, Kind.Other, 0, actionId: 0));
        Assert.False(HotbarMatcher.Matches(Kind.Action, 0, Kind.Action, 0, actionId: 0));
    }

    /// <summary>After a Hasty Touch lands, the game shows the player's Hasty Touch slot as Daring Touch.</summary>
    [Fact]
    public void DaringTouch_IsFound_InTheSlotThatNowAppearsAsIt()
    {
        Assert.True(HotbarMatcher.Matches(Kind.CraftAction, HastyTouch, Kind.CraftAction, DaringTouch, DaringTouch));
    }

    /// <summary>...and even if the apparent action has not caught up yet, the Hasty Touch slot is still the one to press.</summary>
    [Fact]
    public void DaringTouch_FallsBackToTheHastyTouchSlot()
    {
        Assert.Equal(CraftAction.HastyTouch, HotbarMatcher.FallbackFor(CraftAction.DaringTouch));

        Assert.True(HotbarMatcher.Matches(Kind.CraftAction, HastyTouch, Kind.CraftAction, HastyTouch, DaringTouch, fallbackId: HastyTouch));
        Assert.False(HotbarMatcher.Matches(Kind.CraftAction, HastyTouch, Kind.CraftAction, HastyTouch, DaringTouch));
    }

    [Fact]
    public void OnlyDaringTouch_HasAFallback()
    {
        foreach (var action in Enum.GetValues<CraftAction>())
        {
            if (action != CraftAction.DaringTouch)
                Assert.Null(HotbarMatcher.FallbackFor(action));
        }
    }
}
