using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

public class JobChangeTests
{
    private const uint Carpenter = 8;
    private const uint Blacksmith = 9;

    private DateTime _now = new(2026, 11, 12, 18, 0, 0, DateTimeKind.Utc);

    private JobChangeCommand Tick(JobChange change, uint job, bool logOpen = false, bool canSwitch = true, double advanceSeconds = 1)
    {
        _now = _now.AddSeconds(advanceSeconds);
        return change.Tick(new JobChangeObservation(job, logOpen, canSwitch), _now);
    }

    [Fact]
    public void AlreadyOnTheJob_IsTrueAtOnce_AndNothingIsPending()
    {
        var change = new JobChange();

        Assert.True(change.Request(Carpenter, currentJob: Carpenter, hasGearset: true, busy: false));
        Assert.False(change.Pending);
        Assert.Equal(JobChangeCommand.None, Tick(change, Carpenter));
    }

    [Theory]
    [InlineData(0u, 8u, true, false)]  // no job named
    [InlineData(9u, 0u, true, false)]  // not logged in
    [InlineData(9u, 8u, false, false)] // no gearset saved for it
    [InlineData(9u, 8u, true, true)]   // something of ours has the character
    public void ARequestThatCannotBeMet_IsRefused_AndChangesNothing(uint job, uint current, bool gearset, bool busy)
    {
        var change = new JobChange();

        Assert.False(change.Request(job, current, gearset, busy));
        Assert.False(change.Pending);
    }

    [Fact]
    public void TheLogIsClosed_ThenTheGearsetEquipped_ThenTheJobWatched_ThenTheStatsGivenTheirMoment()
    {
        var change = new JobChange();
        Assert.True(change.Request(Blacksmith, Carpenter, hasGearset: true, busy: false));
        Assert.True(change.Pending);

        Assert.Equal(JobChangeCommand.CloseLog, Tick(change, Carpenter, logOpen: true, canSwitch: false));
        Assert.Equal(JobChangeCommand.None, Tick(change, Carpenter, canSwitch: false)); // still leaving the stance
        Assert.Contains("Waiting", change.Status);
        Assert.Equal(JobChangeCommand.Equip, Tick(change, Carpenter));
        Assert.Equal(JobChangeCommand.None, Tick(change, Carpenter)); // the equip is given its time

        Assert.Equal(JobChangeCommand.None, Tick(change, Blacksmith)); // the job changed: now the stats
        Assert.True(change.Pending);
        Tick(change, Blacksmith, advanceSeconds: 2);

        Assert.False(change.Pending);
        Assert.Equal("On the job asked for.", change.Status);
    }

    [Fact]
    public void AnEquipThatDoesNotTake_IsTriedAgain_ThenGivenUp()
    {
        var change = new JobChange();
        change.Request(Blacksmith, Carpenter, true, false);

        var equips = 0;
        for (var i = 0; i < 60 && change.Pending; i++)
        {
            if (Tick(change, Carpenter) == JobChangeCommand.Equip)
                equips++;
        }

        Assert.Equal(JobChange.MaxEquipAttempts, equips);
        Assert.False(change.Pending);
        Assert.Contains("Gave up", change.Status);
    }

    [Fact]
    public void ALogThatWillNotClose_EndsTheRequest_SoIsBusyCanFall()
    {
        var change = new JobChange();
        change.Request(Blacksmith, Carpenter, true, false);

        var closes = 0;
        for (var i = 0; i < 60 && change.Pending; i++)
        {
            if (Tick(change, Carpenter, logOpen: true, canSwitch: false) == JobChangeCommand.CloseLog)
                closes++;
        }

        Assert.Equal(JobChange.MaxCloseAttempts, closes);
        Assert.False(change.Pending);
        Assert.Contains("would not close", change.Status);
    }

    [Fact]
    public void AGameThatNeverBecomesFree_EndsTheRequestAtItsDeadline()
    {
        var change = new JobChange();
        change.Request(Blacksmith, Carpenter, true, false);

        for (var i = 0; i < 100 && change.Pending; i++)
            Assert.Equal(JobChangeCommand.None, Tick(change, Carpenter, canSwitch: false));

        Assert.False(change.Pending);
        Assert.Contains("in time", change.Status);
    }

    [Fact]
    public void AskedAgainWhilePending_TheSameJobIsStillTrue_AnotherIsRefused_AndCancelDropsIt()
    {
        var change = new JobChange();
        change.Request(Blacksmith, Carpenter, true, false);

        Assert.True(change.Request(Blacksmith, Carpenter, true, busy: true)); // it is our own request that makes us busy
        Assert.False(change.Request(Carpenter + 2, Carpenter, true, busy: true));
        Assert.Equal(Blacksmith, change.Wanted);

        change.Cancel();
        Assert.False(change.Pending);
        Assert.Equal(JobChangeCommand.None, Tick(change, Carpenter));
    }
}
