using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Features.Crafting.Solver;

/// <summary>
/// Whoever answers "what now?" for one synthesis. Two exist: <see cref="LiveEvaluator"/> (a Raphael
/// plan, re-scored live - every ordinary recipe) and
/// <see cref="Hephaestus.Features.Crafting.Expert.ExpertAdvisor"/> (expert recipes, where no fixed plan
/// survives the conditions). One instance per craft; never called from two threads at once.
/// </summary>
public interface ICraftAdvisor
{
    /// <summary>An action the game accepted, ours or the player's - in order, before the next <see cref="Recommend"/>.</summary>
    void Observe(CraftAction executed);

    /// <param name="excluded"><see cref="ActionMask"/> bits of actions the game keeps refusing: not to be recommended again.</param>
    Recommendation Recommend(in CraftState state, Condition condition, ulong excluded = 0);
}
