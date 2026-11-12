using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Tests.Sandbox.Expert;

namespace Hephaestus.Tests.Features.Crafting.Expert;

public class ExpertTuningBookTests
{
    /// <summary>Runs a tuning inline, so a test sees its result at once.</summary>
    private static Task Inline(Action work)
    {
        work();
        return Task.CompletedTask;
    }

    private static readonly ExpertProblem Biocoke = MasterMission.All[2].Problem;

    [Fact]
    public void TheThreeMasterShapes_HaveAPreset_AndAnythingElseStartsFromTheDefaults()
    {
        foreach (var mission in MasterMission.All)
            Assert.NotEqual("defaults", ExpertTuningBook.Preset(mission.Problem).Changes());

        var other = Biocoke with { Parameters = Biocoke.Parameters with { MaxProgress = 1234 } };
        Assert.Equal("defaults", ExpertTuningBook.Preset(other).Changes());
    }

    [Fact]
    public void WithTuningOff_ThePresetIsPlayed_AndNothingRuns()
    {
        var ran = 0;
        var book = new ExpertTuningBook(work =>
        {
            ran++;
            return Inline(work);
        });

        var tuning = book.For(Biocoke, tune: false, threads: 1);

        Assert.Equal(0, ran);
        Assert.Equal(ExpertTuningBook.Preset(Biocoke).Changes(), tuning().Changes());
        Assert.Contains("switched off", book.Status);
    }

    [Fact]
    public void ARecipeIsTunedOnce_AndTheAnswerIsNoWorseThanWhereItStarted()
    {
        var ran = 0;
        var book = new ExpertTuningBook(work =>
        {
            ran++;
            return Inline(work);
        }, deepCrafts: 0); // the quick stage alone

        var tuning = book.For(Biocoke, tune: true, threads: 2);
        var again = book.For(Biocoke, tune: true, threads: 2);

        Assert.Equal(1, ran);
        Assert.Equal(tuning().Changes(), again().Changes());
        Assert.Contains("tuned in", book.Status);

        // Judged the way the book judges: the same simulated crafts, points less time, one use of the duty action.
        var problems = new[] { Biocoke };
        double Mean(ExpertTuning t) => ExpertTuner.Scores(problems, t, 1, ExpertTuningBook.Crafts, ExpertTuningBook.TimeValue, dutyUses: 1).Average();
        Assert.True(Mean(tuning()) >= Mean(ExpertTuningBook.Preset(Biocoke)), "the tuning made the recipe worse on its own crafts");
        Assert.Equal(ExpertTuning.Default[Knob.DutyUses], tuning()[Knob.DutyUses]); // the mission's stock is not the tuner's to spend
    }

    [Fact]
    public void TheDeepStage_StartsFromTheQuickOne_AndIsNoWorseOnItsOwnCrafts()
    {
        const int Deep = 300;
        var quick = new ExpertTuningBook(Inline, deepCrafts: 0).For(Biocoke, tune: true, threads: 2)();
        var book = new ExpertTuningBook(Inline, deepCrafts: Deep);
        var both = book.For(Biocoke, tune: true, threads: 2)();

        Assert.Contains("both stages", book.Status);

        // The deep stage judges on crafts the quick one never saw (seeds 100,001 on).
        var problems = new[] { Biocoke };
        double Mean(ExpertTuning t) => ExpertTuner.Scores(problems, t, 100_001, Deep, ExpertTuningBook.TimeValue, dutyUses: 1).Average();
        Assert.True(Mean(both) >= Mean(quick), "the deep stage made the recipe worse on its own crafts");
    }

    [Fact]
    public void AnotherCrafter_IsAnotherEntry()
    {
        var stronger = Biocoke with { Parameters = Biocoke.Parameters with { MaxCp = Biocoke.Parameters.MaxCp + 100 } };

        Assert.NotEqual(ExpertTuningBook.Key(Biocoke), ExpertTuningBook.Key(stronger));
        Assert.Equal(ExpertTuningBook.Key(Biocoke), ExpertTuningBook.Key(Biocoke with { Name = "the same item, another trade" }));
    }

    [Fact]
    public void ATuningThatCannotStart_LeavesThePresetInPlace_AndSaysSo()
    {
        var book = new ExpertTuningBook(_ => throw new InvalidOperationException("no threads today"));

        var tuning = book.For(Biocoke, tune: true, threads: 1);

        Assert.Equal(ExpertTuningBook.Preset(Biocoke).Changes(), tuning().Changes());
        Assert.Contains("could not start", book.Status);
    }
}
