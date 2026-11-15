using Hephaestus.Ipc;

namespace Hephaestus.Tests.Ipc;

public class QueueStatusDtoTests
{
    [Fact]
    public void TheStatusSentence_IsTheQueuesWhileItHasTheCharacter()
    {
        Assert.Equal("queue", QueueStatusDto.StatusLine(queueActive: true, queueHasList: true, "queue", runActive: true, runCanResume: false, "run"));
    }

    [Fact]
    public void ARunOfItsOwn_GoingOrHalted_SpeaksForItself_EvenWithAnOldQueueListAround()
    {
        // The bug this pins: with a finished list still loaded, a halted Craft X / CraftSelected run was reported in the queue's stale words.
        Assert.Equal("run", QueueStatusDto.StatusLine(false, queueHasList: true, "queue", runActive: true, runCanResume: false, "run"));
        Assert.Equal("gear is at 12%", QueueStatusDto.StatusLine(false, queueHasList: true, "Done.", runActive: false, runCanResume: true, "gear is at 12%"));
    }

    [Fact]
    public void WithNothingGoing_WhoeverSpokeLast()
    {
        Assert.Equal("queue", QueueStatusDto.StatusLine(false, queueHasList: true, "queue", false, false, "run"));
        Assert.Equal("run", QueueStatusDto.StatusLine(false, queueHasList: false, "queue", false, false, "run"));
    }

    [Fact]
    public void TheNewFields_ReadAsNothingWrong_UntilTheyAreFilled()
    {
        var status = new QueueStatusDto();

        Assert.Equal("None", status.RunHalt);
        Assert.Equal(100, status.GearConditionPercent);
        Assert.Equal(0, status.RunGearFloorPercent); // no floor under the gate unless a caller asked for one
    }
}
