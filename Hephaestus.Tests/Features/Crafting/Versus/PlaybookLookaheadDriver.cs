using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Tests.Features.Crafting.Versus;

/// <summary>
/// AN EXPERIMENT, not a contestant either plugin ships: live lookahead over a PLAYBOOK instead of over
/// a fixed rotation. Each step it asks "what if I pressed X now, and played the expert playbook from
/// there on?" for every legal X, under the same handful of sampled futures (conditions and success
/// rolls), and presses the X with the best outcome - leaving the playbook only when that is measurably
/// better. The playbook here is Artisan's Expert Recipe Solver, because it is at hand; the question the
/// experiment answers is what Hephaestus's own shape (a reference to follow, re-scored live against the
/// real state) is worth on expert recipes once the reference is a policy that knows the long game.
///
/// What it knows that the live evaluator does not: the outcome it scores is DELIVERY - a craft finished
/// below the recipe's quality demand is a failure - and its playouts run under rolled conditions with
/// actions that can fail, not under Normal with only safe ones.
/// </summary>
internal sealed class PlaybookLookaheadDriver : IVersusDriver
{
    private const int MaxMoves = 160;

    /// <summary>How much better (in delivery probability) an action must look before the playbook is overruled: sampling noise is not a reason.</summary>
    private const double Margin = 0.04;

    /// <summary>...and by this many standard errors of the paired difference.</summary>
    private const double Confidence = 2.5;

    private static readonly CraftAction[] Actions = Enum.GetValues<CraftAction>();

    private readonly CraftParameters _parameters;
    private readonly float[] _odds;
    private readonly bool _expert;
    private readonly int _requiredQuality;
    private readonly int _samples;
    private readonly ArtisanDriver _playbook; // follows the real craft
    private readonly Random _seeds;

    public PlaybookLookaheadDriver(VersusScenario scenario, ArtisanDriver playbook, int samples, int seed)
    {
        _parameters = scenario.Parameters;
        _odds = ConditionOdds.ForRecipe(scenario.Recipe.ConditionsFlag, _parameters.JobLevel);
        _expert = ConditionOdds.IsExpert(scenario.Recipe.ConditionsFlag);
        _requiredQuality = scenario.TargetQuality;
        _samples = samples;
        _playbook = playbook;
        _seeds = new Random(seed);
    }

    /// <summary>Decisions where the lookahead overruled the playbook, of all decisions.</summary>
    public (int Overruled, int Decisions) Tally { get; private set; }

    public VersusMove? Next(in CraftState state, Condition condition, int finalAppraisalLeft)
    {
        var world = new VersusWorld { State = state, Condition = condition, Appraisal = finalAppraisalLeft };
        var planned = _playbook.Next(state, condition, finalAppraisalLeft); // also lets the playbook update its own flags

        var candidates = new List<VersusMove>();
        if (planned is { } p)
            candidates.Add(p);
        foreach (var action in Actions)
        {
            var move = new VersusMove(action);
            if (planned == move || CraftSimulator.Check(_parameters, state, action, condition) != ActionError.None)
                continue;
            if (state.Quality >= _requiredQuality && IsQualityOnly(action))
                continue;
            candidates.Add(move);
        }

        if (candidates.Count == 0)
            return null;

        // The same futures for every candidate, so they are compared on equal luck.
        var futures = new int[_samples];
        for (var i = 0; i < futures.Length; i++)
            futures[i] = _seeds.Next();

        // Score every candidate on every future. The playbook's own move is the reference: another move
        // overrules it only when it is better by more than the sampling noise of the PAIRED differences
        // (same futures, so most of the luck cancels) - picking the luckiest of twenty noisy averages
        // would otherwise be worse than not looking at all.
        var scores = new double[candidates.Count][];
        for (var c = 0; c < candidates.Count; c++)
        {
            scores[c] = new double[futures.Length];
            for (var f = 0; f < futures.Length; f++)
                scores[c][f] = PlayOut(world, candidates[c], takenUnder: condition, new Random(futures[f]));
        }

        var choice = 0;
        if (planned is null)
        {
            for (var c = 1; c < candidates.Count; c++)
            {
                if (scores[c].Average() > scores[choice].Average())
                    choice = c;
            }
        }
        else
        {
            var bestGain = 0.0;
            for (var c = 1; c < candidates.Count; c++)
            {
                var mean = 0.0;
                for (var f = 0; f < futures.Length; f++)
                    mean += scores[c][f] - scores[0][f];
                mean /= futures.Length;

                var variance = 0.0;
                for (var f = 0; f < futures.Length; f++)
                {
                    var d = scores[c][f] - scores[0][f] - mean;
                    variance += d * d;
                }

                var standardError = Math.Sqrt(variance / Math.Max(1, futures.Length - 1) / futures.Length);
                if (mean > Math.Max(Margin, Confidence * standardError) && mean > bestGain)
                    (choice, bestGain) = (c, mean);
            }
        }

        Tally = (Tally.Overruled + (choice != 0 && planned != null ? 1 : 0), Tally.Decisions + 1);
        return candidates[choice];
    }

    public void Observed(VersusMove move, Condition takenUnder, bool success) => _playbook.Observed(move, takenUnder, success);

    /// <summary>Press <paramref name="first"/>, then let the playbook play to the end of one sampled future. 1 = delivered.</summary>
    private double PlayOut(VersusWorld world, VersusMove first, Condition takenUnder, Random random)
    {
        var playbook = _playbook.Clone();
        if (VersusPlayer.Apply(_parameters, _odds, _expert, ref world, first, random, out var success) != null)
            return double.NegativeInfinity;
        playbook.Observed(first, takenUnder, success);

        for (var moves = 0; moves < MaxMoves && !CraftSimulator.IsFinal(_parameters, world.State); moves++)
        {
            if (playbook.Next(world.State, world.Condition, world.Appraisal) is not { } move)
                break;

            var under = world.Condition;
            if (VersusPlayer.Apply(_parameters, _odds, _expert, ref world, move, random, out success) != null)
                break;
            playbook.Observed(move, under, success);
        }

        if (CraftSimulator.IsSuccess(_parameters, world.State) && world.State.Quality >= _requiredQuality)
            return 1.0;

        // Among failures, the nearer miss: a little for quality, less still without the progress.
        var quality = Math.Min(1.0, world.State.Quality / (double)Math.Max(1, _requiredQuality));
        return (CraftSimulator.IsSuccess(_parameters, world.State) ? 0.2 : 0.1) * quality;
    }

    private static bool IsQualityOnly(CraftAction action) => action is CraftAction.BasicTouch or CraftAction.StandardTouch
        or CraftAction.AdvancedTouch or CraftAction.PrudentTouch or CraftAction.PreparatoryTouch or CraftAction.PreciseTouch
        or CraftAction.HastyTouch or CraftAction.DaringTouch or CraftAction.Reflect or CraftAction.TrainedFinesse
        or CraftAction.RefinedTouch or CraftAction.ByregotsBlessing or CraftAction.GreatStrides or CraftAction.Innovation;
}
