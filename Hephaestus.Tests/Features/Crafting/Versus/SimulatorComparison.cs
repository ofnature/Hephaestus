using Hephaestus.Features.Crafting.Simulation;
using ArtisanSimulator = Artisan.CraftingLogic.Simulator;
using Skills = Artisan.RawInformation.Character.Skills;

namespace Hephaestus.Tests.Features.Crafting.Versus;

/// <summary>One way the two simulators answered differently from the same state.</summary>
/// <param name="Kind">What differed: "usable", or the field (progress, quality, cp, durability, a buff).</param>
internal sealed record SimulatorDifference(string Kind, CraftAction Action, Condition Condition, string Ours, string Theirs, string State);

/// <summary>
/// Hephaestus's simulator against Artisan's, one step at a time from identical states: random legal
/// crafts on a scenario, and after every action "would Artisan's simulator have said the same?".
/// The benchmark's world is OURS (pinned to raphael-sim's in-game-verified vectors); Artisan's is
/// what ITS solvers predict with. Where the two differ, one of them is wrong about the game — and
/// either way it is something Artisan's solvers believe that the benchmark's world does not do.
/// </summary>
internal static class SimulatorComparison
{
    public static List<SimulatorDifference> Run(VersusScenario scenario, int crafts, int seed)
    {
        var parameters = scenario.Parameters;
        var craft = ArtisanDriver.Craft(scenario);
        var odds = ConditionOdds.ForRecipe(scenario.Recipe.ConditionsFlag, parameters.JobLevel);
        var expert = ConditionOdds.IsExpert(scenario.Recipe.ConditionsFlag);
        var random = new Random(seed);
        var actions = Enum.GetValues<CraftAction>();
        var found = new List<SimulatorDifference>();

        for (var i = 0; i < crafts; i++)
        {
            var bridge = new ArtisanDriver(craft, new Artisan.CraftingLogic.Solvers.StandardSolver()); // only its bookkeeping is used
            var state = CraftSimulator.Initial(parameters);
            var condition = Condition.Normal;

            for (var step = 0; step < 60 && !CraftSimulator.IsFinal(parameters, state); step++)
            {
                var action = actions[random.Next(actions.Length)];
                if (action is CraftAction.DaringTouch && !state.Expedience)
                    continue; // Hasty Touch under another name; only meaningful after a Hasty Touch landed

                var theirStep = bridge.Snapshot(state, condition);
                var skill = ArtisanDriver.ToSkill(action);
                var oursUsable = CraftSimulator.Check(parameters, state, action, condition) == ActionError.None;
                var theirsUsable = ArtisanSimulator.CanUseAction(craft, theirStep, skill);
                if (oursUsable != theirsUsable)
                {
                    found.Add(new("usable", action, condition, oursUsable ? "yes" : CraftSimulator.Check(parameters, state, action, condition).ToString(),
                        theirsUsable ? "yes" : "no", Describe(state)));
                }

                if (!oursUsable || !theirsUsable)
                    continue;

                var success = random.Next(100) < CraftSimulator.SuccessPercent(action, condition);
                CraftSimulator.Use(parameters, state, action, condition, success, out var ours);
                var (_, theirs) = ArtisanSimulator.Execute(craft, theirStep, skill, success ? 0f : 1f, 1f);

                void Compare(string kind, int mine, int its)
                {
                    if (mine != its)
                        found.Add(new(kind, action, condition, mine.ToString(), its.ToString(), Describe(state)));
                }

                var finished = CraftSimulator.IsFinal(parameters, ours);
                Compare("progress", ours.Progress, Math.Min(parameters.MaxProgress, theirs.Progress));
                Compare("quality", ours.Quality, Math.Min(parameters.MaxQuality, theirs.Quality));
                Compare("cp", ours.Cp, theirs.RemainingCP);
                Compare("durability", ours.Durability, Math.Max(0, theirs.Durability));
                if (!finished) // neither simulator promises anything about buffs on a craft that is over
                {
                    Compare("inner quiet", ours.InnerQuiet, theirs.IQStacks);
                    Compare("waste not", ours.WasteNot, theirs.WasteNotLeft);
                    Compare("manipulation", ours.Manipulation, theirs.ManipulationLeft);
                    Compare("great strides", ours.GreatStrides, theirs.GreatStridesLeft);
                    Compare("innovation", ours.Innovation, theirs.InnovationLeft);
                    Compare("veneration", ours.Veneration, theirs.VenerationLeft);
                    Compare("muscle memory", ours.MuscleMemory, theirs.MuscleMemoryLeft);
                    Compare("trained perfection active", ours.TrainedPerfectionActive ? 1 : 0, theirs.TrainedPerfectionActive ? 1 : 0);
                    Compare("expedience", ours.Expedience ? 1 : 0, theirs.ExpedienceLeft > 0 ? 1 : 0);
                }

                state = ours;
                var takenUnder = condition;
                if (CraftSimulator.SpendsStep(action))
                    condition = ConditionOdds.Next(odds, expert, condition, random.NextSingle());
                bridge.Observed(new VersusMove(action), takenUnder, success);
            }
        }

        return found;
    }

    private static string Describe(in CraftState s)
        => $"P{s.Progress} Q{s.Quality} D{s.Durability} CP{s.Cp} IQ{s.InnerQuiet} WN{s.WasteNot} Inno{s.Innovation} GS{s.GreatStrides} Vene{s.Veneration} MuMe{s.MuscleMemory} Manip{s.Manipulation} combo {s.Combo}{(s.TrainedPerfectionActive ? " TP" : "")}";
}
