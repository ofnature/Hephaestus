using Hephaestus.Features.Queue;

namespace Hephaestus.Tests.Features.Queue;

public class StopAfterRunTests
{
    private static readonly StopObservation Working = new(Working: true, LogOpen: false, CanMove: false);
    private static readonly StopObservation InTheLog = new(Working: false, LogOpen: true, CanMove: false);
    private static readonly StopObservation Settling = new(Working: false, LogOpen: false, CanMove: false);
    private static readonly StopObservation Free = new(Working: false, LogOpen: false, CanMove: true);

    private DateTime _now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private StopCommand Tick(StopAfterRun stop, StopObservation obs, double advanceSeconds = 1)
    {
        _now = _now.AddSeconds(advanceSeconds);
        return stop.Tick(obs, _now);
    }

    /// <summary>Nothing to stop: refused, and nothing is left behind to stop the next start.</summary>
    [Fact]
    public void Idle_IsRefused_AndChangesNothing()
    {
        var stop = new StopAfterRun();

        Assert.False(stop.Request(busy: false));
        Assert.False(stop.Pending);
        Assert.Equal(StopCommand.None, Tick(stop, InTheLog));
    }

    [Fact]
    public void Busy_IsAccepted_AndAskingAgain_IsNotAToggle()
    {
        var stop = new StopAfterRun();

        Assert.True(stop.Request(busy: true));
        Assert.True(stop.Request(busy: true));
        Assert.Equal(StopCommand.StopWork, Tick(stop, Working));

        Assert.True(stop.Request(busy: true));
        Assert.Equal(StopCommand.None, Tick(stop, Working)); // told once
        Assert.True(stop.Pending);
    }

    /// <summary>A long synthesis is waited out — never cut short — then the stance is left, read off the game.</summary>
    [Fact]
    public void WaitsForTheWorkToEnd_ThenClosesTheLog_AndEndsOnceTheCharacterIsFree()
    {
        var stop = new StopAfterRun();
        stop.Request(busy: true);
        Tick(stop, Working);

        for (var i = 0; i < 60; i++)
            Assert.Equal(StopCommand.None, Tick(stop, Working, advanceSeconds: 5));
        Assert.True(stop.Pending);

        Assert.Equal(StopCommand.None, Tick(stop, Settling)); // the log is away for a moment after the synthesis
        Assert.Equal(StopCommand.CloseLog, Tick(stop, InTheLog));
        Tick(stop, Settling);                                    // the tools are being put away
        Tick(stop, Free);
        Assert.True(stop.Pending);                               // free is believed once it has lasted
        Tick(stop, Free);

        Assert.False(stop.Pending);
        Assert.Contains("free to move", stop.Status);
    }

    [Fact]
    public void AStanceThatIsNeverLeft_EndsOnTheDeadline()
    {
        var stop = new StopAfterRun();
        stop.Request(busy: true);
        Tick(stop, Working);

        for (var i = 0; i < 40 && stop.Pending; i++)
            Tick(stop, Settling);

        Assert.False(stop.Pending);
        Assert.Contains("could not be left in time", stop.Status);
    }

    [Fact]
    public void ALogThatWillNotClose_IsGivenUpOn_AndSaysSo()
    {
        var stop = new StopAfterRun();
        stop.Request(busy: true);
        Tick(stop, Working);

        var closes = 0;
        for (var i = 0; i < 40 && stop.Pending; i++)
        {
            if (Tick(stop, InTheLog, advanceSeconds: 0.7) == StopCommand.CloseLog)
                closes++;
        }

        Assert.False(stop.Pending);
        Assert.Equal(QueueRun.MaxCloseAttempts, closes);
        Assert.Contains("would not close", stop.Status);
    }

    /// <summary>Ours had all ended, so work showing up again is a fresh start (the window, another plugin): it wins.</summary>
    [Fact]
    public void NewWork_AfterOursEnded_CancelsTheStop()
    {
        var stop = new StopAfterRun();
        stop.Request(busy: true);
        Tick(stop, Working);
        Assert.Equal(StopCommand.CloseLog, Tick(stop, InTheLog));

        Assert.Equal(StopCommand.None, Tick(stop, Working with { LogOpen = true }));

        Assert.False(stop.Pending);
        Assert.Contains("cancelled", stop.Status);
    }

    [Fact]
    public void Cancel_ForgetsTheRequest()
    {
        var stop = new StopAfterRun();
        stop.Request(busy: true);
        Tick(stop, Working);

        stop.Cancel("the queue was started");

        Assert.False(stop.Pending);
        Assert.Equal(StopCommand.None, Tick(stop, InTheLog));
    }
}
