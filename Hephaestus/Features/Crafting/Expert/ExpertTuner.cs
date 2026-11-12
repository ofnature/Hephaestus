using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hephaestus.Features.Crafting.Expert;

/// <summary>
/// The loop a person would do by hand - change one thing, craft a few thousand items, average, keep
/// the change if it helped, change the next thing - done by the machine, on SIMULATED crafts.
///
/// Every setting is judged on THE SAME crafts (same seeds, so the same conditions and rolls), which
/// is what lets a small real improvement show through the luck: a change is kept only when the
/// craft-by-craft difference is clear of its own noise (two standard errors). One knob at a time,
/// round after round, until a whole round changes nothing.
///
/// Used by the tests' sandbox (to find the defaults and to measure) and by the plugin before an
/// expert craft (<see cref="ExpertTuningBook"/>): a recipe's best knobs depend on the recipe AND on
/// the crafter's stats, and a second of simulation finds them.
/// </summary>
public static class ExpertTuner
{
    /// <summary>The <see cref="ExpertSearch.Value"/> of each craft: problem by problem, seed by seed.</summary>
    /// <param name="timeValue">Scored collectables: what a second is worth, in points (see <see cref="SearchSettings.TimeValue"/>).</param>
    /// <param name="dutyUses">Uses of the problem's duty action each craft is lent; -1 = what <see cref="Knob.DutyUses"/> says.</param>
    /// <param name="threads">Most threads to use; 0 = all of them. The plugin never passes 0: the game needs its cores.</param>
    public static double[] Scores(
        IReadOnlyList<ExpertProblem> problems, ExpertTuning tuning, int firstSeed, int crafts,
        double timeValue = 0, int dutyUses = 0, int threads = 0, CancellationToken cancel = default)
    {
        var scores = new double[problems.Count * crafts];
        var options = new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : -1, CancellationToken = cancel };
        Parallel.For(0, scores.Length, options, i =>
        {
            var problem = problems[i / crafts];
            var uses = problem.Duty is null ? 0 : dutyUses < 0 ? tuning[Knob.DutyUses] : dutyUses;
            var result = ExpertPlayer.Play(problem, tuning, firstSeed + i % crafts, dutyUses: uses);
            scores[i] = ExpertSearch.Value(problem, result.Final, result.Quarters, timeValue);
        });
        return scores;
    }

    /// <summary>Share of crafts delivered, in percent.</summary>
    public static double Delivered(double[] scores) => scores.Count(s => s >= 1.0) * 100.0 / scores.Length;

    /// <param name="crafts">Crafts per problem per setting tried.</param>
    /// <param name="log">Told of every change kept.</param>
    /// <param name="timeValue">What a second is worth, in points (a delivered item is worth 1,000); 0 = delivery alone is tuned for.</param>
    public static ExpertTuning Tune(
        IReadOnlyList<ExpertProblem> problems, ExpertTuning start, int crafts, Action<string> log, int firstSeed = 1, int maxRounds = 8,
        double timeValue = 0)
        => Tune(
            tuning => Scores(problems, tuning, firstSeed, crafts, timeValue), start, log,
            scores => timeValue > 0
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"worth {scores.Average() * 1000:F0}")
                : $"delivered {Delivered(scores):F1}%",
            maxRounds);

    /// <summary>The same loop over anything that can be scored trial by trial - whole missions, say.</summary>
    /// <param name="evaluate">One score per trial; the SAME trials, in the same order, whatever the setting.</param>
    /// <param name="describe">How a set of scores reads in the log.</param>
    /// <param name="skip">Knobs to leave alone.</param>
    public static ExpertTuning Tune(
        Func<ExpertTuning, double[]> evaluate, ExpertTuning start, Action<string> log, Func<double[], string> describe,
        int maxRounds = 8, Func<Knob, bool>? skip = null)
    {
        var best = start;
        var bestScores = evaluate(best);
        for (var round = 1; round <= maxRounds; round++)
        {
            var changed = false;
            foreach (var knob in ExpertTuning.Knobs)
            {
                if (skip?.Invoke(knob.Knob) == true)
                    continue;

                ExpertTuning? winner = null;
                double[]? winnerScores = null;
                var winnerGain = 0.0;
                foreach (var choice in knob.Choices)
                {
                    if (choice == best[knob.Knob])
                        continue;

                    var candidate = best.With(knob.Knob, choice);
                    var scores = evaluate(candidate);
                    var (gain, error) = Paired(scores, bestScores);
                    if (gain > winnerGain && gain > 0.001 && gain > 2.0 * error)
                        (winner, winnerScores, winnerGain) = (candidate, scores, gain);
                }

                if (winner is null)
                    continue;

                log($"round {round}: {knob.Knob} {best[knob.Knob]} -> {winner[knob.Knob]}, {describe(bestScores)} -> {describe(winnerScores!)}");
                (best, bestScores, changed) = (winner, winnerScores!, true);
            }

            if (!changed)
                break;
        }

        return best;
    }

    /// <summary>Mean of the craft-by-craft differences, and its standard error.</summary>
    public static (double Mean, double Error) Paired(double[] scores, double[] reference)
    {
        var sum = 0.0;
        var squares = 0.0;
        for (var i = 0; i < scores.Length; i++)
        {
            var difference = scores[i] - reference[i];
            sum += difference;
            squares += difference * difference;
        }

        var mean = sum / scores.Length;
        var variance = Math.Max(0.0, (squares - scores.Length * mean * mean) / Math.Max(1, scores.Length - 1));
        return (mean, Math.Sqrt(variance / scores.Length));
    }
}
