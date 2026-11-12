using Hephaestus.Features.Crafting.Expert;
using Hephaestus.Features.Crafting.Simulation;
using Hephaestus.Tests.Features.Crafting.Versus;

namespace Hephaestus.Tests.Sandbox.Expert;

/// <param name="Score">Reaching it ends the stage: the multiplier rises and the mission hands out what follows.</param>
/// <param name="Seconds">Added to the time limit.</param>
/// <param name="Uses">Added to the stock of the duty action.</param>
internal readonly record struct MissionCheckpoint(int Score, int Seconds, int Uses);

/// <summary>
/// A Cosmic Exploration "Master" (tool mastery) mission: make as many of one expert collectable as fit in the time,
/// for points. FROM THE GAME'S SHEETS (client 2026.11.03): the time limit (WKSMissionUnit.MissionTime), the
/// checkpoints (WKSMissionToDo.BronzeScore of the first two stages), the multiplier of each stage (100 / 150 / 200%),
/// the duty action and its uses at the start, and what a checkpoint adds (WKSMissionToDoRecover: seconds, uses).
/// The points per item are player-reported (<see cref="ExpertProblem.Points"/>).
/// </summary>
/// <param name="Scenario">The recipe at the crafter's stats; the same three recipe shapes serve all eight trades.</param>
/// <param name="Specialist">The crafter may use Heart and Soul and Quick Innovation; null = as the environment says.</param>
internal sealed record MasterMission(
    string Name, VersusScenario Scenario, CraftAction Duty, int Seconds, int StartUses, MissionCheckpoint[] Checkpoints, bool? Specialist = null)
{
    public static readonly int[] MultiplierPercent = { 100, 150, 200 };

    public ExpertProblem Problem { get; } = SandboxProblems.From(Scenario, Duty, Specialist);

    /// <summary>The same mission for another crafter. Not a with-expression: that would keep the old crafter in <see cref="Problem"/>.</summary>
    public MasterMission For(VersusScenario scenario, bool? specialist = null) => new(Name, scenario, Duty, Seconds, StartUses, Checkpoints, specialist ?? Specialist);

    /// <summary>The three missions of the series (Carpenter's are WKSMissionUnit 1395-1397).</summary>
    public static readonly MasterMission[] All =
    {
        new("Master 1: rlvl 773, 5 min, Material Miracle", VersusScenarios.Named("Biofuel773-Melded"), CraftAction.MaterialMiracle, 300, 2,
            new MissionCheckpoint[] { new(1000, 180, 2), new(2500, 300, 2) }),
        new("Master 2: rlvl 774, 4 min, Stellar Steady Hand", VersusScenarios.Named("Compounds774-Melded"), CraftAction.StellarSteadyHand, 240, 4,
            new MissionCheckpoint[] { new(1000, 180, 2), new(2500, 360, 6) }),
        new("Master 3: rlvl 775, 7 min, Material Miracle", VersusScenarios.Named("Biocoke775-Melded"), CraftAction.MaterialMiracle, 420, 4,
            new MissionCheckpoint[] { new(2000, 150, 1), new(3500, 330, 3) }),
    };
}

/// <summary>One item as a mission sees it.</summary>
internal readonly record struct MissionCraft(bool Completed, int Quality, int Quarters, int DutyUsed, int Overruled = 0);

/// <param name="Score">Points with the multipliers - what the mission shows.</param>
/// <param name="Points">Points before the multipliers.</param>
/// <param name="Stage">Checkpoints reached: 0, 1 or 2. Nothing is rewarded at 0.</param>
/// <param name="Seconds">Time spent on the items that counted, the pauses between them included.</param>
/// <param name="Leftover">Seconds of the mission that no item fitted into: the tail an item was started in and not finished.</param>
/// <param name="LastMultiplier">The multiplier, in percent, an item finished in that tail would have had.</param>
internal readonly record struct MissionResult(
    int Score, int Points, int Crafts, int Gold, int Stage, double Seconds, int DutyUsed, int Overruled, double Leftover = 0, int LastMultiplier = 100);

/// <summary>
/// SANDBOX - not in the plugin. Plays a whole <see cref="MasterMission"/>: item after item against the clock,
/// the stock of the duty action lent out craft by craft, checkpoints adding time, uses and the multiplier.
///
/// ASSUMED, to be set against a real run: the clock (<see cref="CraftSimulator.QuarterSeconds"/> per action,
/// times <see cref="Pace"/>), <see cref="OverheadSeconds"/> between items, that an item unfinished when the
/// time runs out counts for nothing, that a Material Miracle still running when an item is finished is lost,
/// and that the multiplier of a stage applies to every item finished after its checkpoint.
/// </summary>
internal static class MasterMissions
{
    /// <summary>Seconds between two items: the last one's finish, the next one's start.</summary>
    public static double OverheadSeconds { get; set; } = 6;

    /// <summary>How much slower (above 1) or faster the real crafter presses than the simulator's clock.</summary>
    public static double Pace { get; set; } = 1;

    /// <param name="seed">Item i of the mission is played on seed * 100 + i, whoever crafts.</param>
    /// <param name="usesPerCraft">How many uses of the duty action an item may take from the stock.</param>
    /// <param name="craft">(seed, uses lent) -> the item.</param>
    /// <param name="lend">(stage, stock) -> uses lent to the next item, in place of "as many as one craft may take while there are any".</param>
    public static MissionResult Play(
        MasterMission mission, int seed, int usesPerCraft, Func<int, int, MissionCraft> craft, Func<int, int, int>? lend = null)
    {
        var problem = mission.Problem;
        double clock = 0, limit = mission.Seconds;
        int score = 0, points = 0, crafts = 0, gold = 0, stage = 0, stock = mission.StartUses, used = 0, overruled = 0;

        for (var i = 0; i < 99; i++)
        {
            var item = craft(seed * 100 + i, Math.Min(stock, lend?.Invoke(stage, stock) ?? usesPerCraft));
            var seconds = item.Quarters / 4.0 * Pace + OverheadSeconds;
            if (clock + seconds > limit)
                break;

            clock += seconds;
            stock -= item.DutyUsed;
            used += item.DutyUsed;
            overruled += item.Overruled;
            crafts++;

            var worth = problem.Points(item.Completed, item.Quality);
            points += worth;
            score += worth * MasterMission.MultiplierPercent[stage] / 100;
            if (item.Completed && item.Quality >= problem.Target)
                gold++;

            while (stage < mission.Checkpoints.Length && score >= mission.Checkpoints[stage].Score)
            {
                limit += mission.Checkpoints[stage].Seconds;
                stock += mission.Checkpoints[stage].Uses;
                stage++;
            }
        }

        return new MissionResult(score, points, crafts, gold, stage, clock, used, overruled, limit - clock, MasterMission.MultiplierPercent[stage]);
    }

    /// <summary>The sandbox solver as the mission's crafter: the policy, alone or under the search.</summary>
    public static MissionResult Play(
        MasterMission mission, ExpertTuning tuning, int seed, SearchSettings? search = null,
        System.Collections.Concurrent.ConcurrentDictionary<string, int>? overrules = null, Func<int, int, int>? lend = null)
        => Play(mission, seed, tuning[Knob.DutyUses], (craftSeed, uses) =>
        {
            var run = ExpertPlayer.Play(mission.Problem, tuning, craftSeed, search, overrules, uses);
            return new MissionCraft(run.Completed, run.Quality, run.Quarters, run.DutyUsed, run.Overruled);
        }, lend);

    /// <summary>Anyone who can sit in the shared world as the mission's crafter - without the duty action, which that world does not deal.</summary>
    public static MissionResult Play(MasterMission mission, int seed, Func<VersusScenario, IVersusDriver> seat)
        => Play(mission, seed, 0, (craftSeed, _) =>
        {
            var scenario = mission.Scenario;
            var run = VersusPlayer.Play(scenario.Parameters, scenario.Recipe.ConditionsFlag, scenario.InitialQuality, craftSeed, seat(scenario));
            var quarters = 0;
            foreach (var step in run.Trace)
                quarters += CraftActionNames.TryParse(step.Action, out var action) ? CraftSimulator.QuarterSeconds(action) : 5; // Final Appraisal
            return new MissionCraft(run.Completed, run.Quality, quarters, 0);
        });

    /// <summary>The mission scores of seeds first..first+count-1, in thousands of points: what the tuner turns the knobs by.</summary>
    public static double[] Scores(MasterMission mission, ExpertTuning tuning, int firstSeed, int count)
    {
        var scores = new double[count];
        Parallel.For(0, count, i => scores[i] = Play(mission, tuning, firstSeed + i).Score / 1000.0);
        return scores;
    }
}
