using System;
using System.Linq;
namespace Hephaestus.Features.Crafting.Expert;

/// <summary>The numbers <see cref="ExpertPolicy"/> decides by. Every one is something the tuner may turn.</summary>
public enum Knob
{
    /// <summary>0 = Muscle Memory, 1 = Reflect.</summary>
    Opener,

    /// <summary>0 = progress first, on any condition; 1 = progress only when the material favours it, quality in between.</summary>
    Style,

    /// <summary>Cast Veneration while more than this many HALF Rapid Synthesis hits of progress are missing; 99 = never.</summary>
    VenerationAt,

    /// <summary>What a point of durability is worth, in tenths of a CP.</summary>
    DurabilityRate,

    /// <summary>What taking a step costs on top of its CP and durability, in CP.</summary>
    StepCost,

    /// <summary>What a stack of Inner Quiet is worth, in tenths of the base quality.</summary>
    IqValue,

    /// <summary>CP taken off the cost of a touch that makes the next one cheaper (Basic > Standard > Advanced, Hasty > Daring).</summary>
    ComboCredit,

    /// <summary>CP never spent on ordinary touches: it belongs to the closing Byregot's Blessing and its buffs.</summary>
    ByregotReserve,

    /// <summary>Inner Quiet at which the closing combination is looked for.</summary>
    FinisherMinIq,

    /// <summary>Cast Manipulation when it is down and durability is at or below this percentage of the maximum; 0 = only when a condition invites it.</summary>
    ManipulationBelow,

    /// <summary>On Pliant, recast Manipulation when it has this many steps left or fewer; -1 = never for that reason.</summary>
    PliantManipulationAt,

    /// <summary>Manipulation on Primed when it is down: 0 = never, 1 = always, more = only with durability at or below this percentage of the maximum.</summary>
    PrimedManipulation,

    /// <summary>Durability that must be missing before Immaculate Mend is worth its CP.</summary>
    ImmaculateMissing,

    /// <summary>On Good, take Tricks of the Trade instead of a touch while CP is below this.</summary>
    TricksBelowCp,

    /// <summary>Inner Quiet from which a Good Omen is met with Great Strides; 11 = never.</summary>
    GreatStridesMinIq,

    /// <summary>CP that must be left over, beyond the reserves, for Innovation to be cast.</summary>
    InnovationSpare,

    /// <summary>1 = Observe while Manipulation is mending and nothing else can be done.</summary>
    ObserveForDurability,

    /// <summary>0 = never; 1 = spend Trained Perfection in the quality phase, before an Innovation; 2 = as soon as a step has nothing better to offer.</summary>
    TrainedPerfection,

    /// <summary>A Good step while progress is still wanted: 0 = Intensive Synthesis, 1 = Tricks of the Trade, 2 = Precise Touch (Tricks when it cannot be afforded).</summary>
    GoodInProgress,

    /// <summary>
    /// Durability's price follows what is left: tenths of a CP per point, times (CP to spare / durability in hand),
    /// never above <see cref="DurabilityRate"/>. 999 = the price is always <see cref="DurabilityRate"/>.
    /// </summary>
    RateSlope,

    /// <summary>Durability an ordinary touch must leave behind for the closing Byregot's Blessing.</summary>
    ByregotDurability,

    /// <summary>
    /// On Good, Tricks of the Trade is taken when the best touch nets less than the 20 CP are worth, the CP valued at
    /// what a plain touch yields per CP, times this many tenths. 0 = a touch whenever there is one.
    /// </summary>
    TricksWeight,

    // ---- Cosmic "Master" missions only: a collectable scored by points, made against the clock, with a duty action. ----

    /// <summary>
    /// Quality aimed at, in percent of the maximum. A scored collectable: never below the top rating (points still
    /// rise above 85%). An ordinary item judged by its HQ chance (<see cref="ExpertProblem.ByHqChance"/>): where the
    /// quality work stops. Nothing else is aimed anywhere but at its demand.
    /// </summary>
    Aim,

    /// <summary>
    /// Stop working on quality and finish when the CP in hand cannot reach the next rating: reach = spare CP x what a
    /// combo'd Advanced Touch under Innovation yields per CP now, times this many tenths. 0 = never give up.
    /// </summary>
    Reach,

    /// <summary>Stellar Steady Hand: 0 = never, 1 = for Rapid Synthesis, 2 = for Hasty and Daring Touch while Inner Quiet builds, 3 = whichever comes first.</summary>
    SteadyHand,

    /// <summary>Material Miracle, whenever a use is in hand and none is running: 0 = never, 1 = from the first step, 2 = once only the closing synthesis is left of the progress, 3 = from eight Inner Quiet.</summary>
    Miracle,

    /// <summary>Uses of the mission's duty action one craft may take from the mission's stock.</summary>
    DutyUses,

    /// <summary>1 = on Good Omen, hold the closing Byregot's Blessing one step (Observe) so that it lands on Good.</summary>
    WaitForGood,

    /// <summary>Inner Quiet from which Great Strides is cast under Innovation for the next touch; 0 = only for a Good Omen and the close.</summary>
    Strides,

    /// <summary>
    /// Great Strides on a Good Omen: 0 = with two steps of Innovation left; 1 = whether Innovation is up or not, and
    /// before Innovation is recast; 2 = as 0, and with nine Inner Quiet or more it comes before everything else,
    /// Innovation or not (the search's choice on half of those steps).
    /// </summary>
    OmenStrides,

    /// <summary>
    /// Where the touch is picked by what it NETS (its quality less what its cost would have made in plain touches)
    /// instead of by quality per CP: 0 = under Great Strides only, 1 = also on Good, 2 = also on Sturdy, 3 = always.
    /// </summary>
    NetOn,

    // ---- Steps as the scarce thing: ordinary recipes, where the CP is ample and the time is what the craft costs. ----

    /// <summary>
    /// 1 = CP and durability are priced by how scarce the CP is: with more in hand than the rest of the craft needs
    /// at a plain touch's rate, they cost that much less beside <see cref="StepCost"/> - the dearer, bigger action wins.
    /// </summary>
    Thrift,

    /// <summary>Rapid Synthesis: 0 = whenever it is the best bet, 1 = only when it lands three times in four or surer (Centered, Stellar Steady Hand).</summary>
    Rapid,

    /// <summary>Waste Not while four (1) or eight (2, Waste Not II) more steps that wear the item are still to come; 0 = never.</summary>
    WasteNot,

    /// <summary>Delicate Synthesis among the touches while progress is still wanted, its progress counted at this many percent of a touch of the same potency; 0 = never.</summary>
    Delicate,

    /// <summary>When durability must be bought back: 0 = Manipulation first, 1 = Immaculate Mend first (one step for all of it) once <see cref="ImmaculateMissing"/> is gone.</summary>
    Mend,

    // ---- Rules the search kept overruling the policy for (the overrule logs of 2026-11-12). ----

    /// <summary>
    /// What Trained Perfection is spent on: 0 = whatever wears the item next, 1 = an action is priced by the durability
    /// it leaves unsaved, so the free one goes to Groundwork or Preparatory Touch and not to the next Rapid Synthesis.
    /// </summary>
    Perfection,

    /// <summary>On Pliant with <see cref="ImmaculateMissing"/> gone: 0 = Manipulation is looked at first, 1 = Immaculate Mend at half price is.</summary>
    PliantMend,

    /// <summary>
    /// 1 = Innovation is cast on any step alike; 0 = a Sturdy or Robust step goes to a touch while one can be made and
    /// Innovation waits; 2 = so does a Centered step (the touches that can fail land three times in four there).
    /// </summary>
    SturdyInnovation,

    /// <summary>1 = Observe followed by Advanced Touch counts among the touches, as one touch that takes two steps.</summary>
    ObserveCombo,

    /// <summary>
    /// Preparatory Touch once Inner Quiet is full (its stacks no longer count, the biggest touch does) and Innovation
    /// or Great Strides lies over it: 0 = only when its yield per CP wins, 1 = on Good, 2 = also on Sturdy and Robust,
    /// 3 = also on Pliant. Where the search took it out of the policy's hands (measured 2026-11-12, Master missions):
    /// a quarter of the Good steps at ten Inner Quiet, none below seven.
    /// </summary>
    Prep,

    /// <summary>1 = when Trained Finesse is all that can still be pressed, durability is bought back first while a mend can be paid for: it buys more touches than the same CP in Trained Finesse.</summary>
    MendBeforeFinesse,

    // ---- Specialist actions. They only exist in a craft whose parameters allow them: the player opted in and holds a delineation. ----

    /// <summary>Heart and Soul: 0 = never, 1 = for Intensive Synthesis while progress is being made under a buff, 2 = for Precise Touch under Innovation, 3 = whichever comes first.</summary>
    HeartAndSoul,

    /// <summary>Quick Innovation: 0 = never, 1 = for the closing Byregot's Blessing when Innovation is down.</summary>
    QuickInnovation,
}

/// <param name="Choices">The values the tuner tries, the default among them.</param>
public sealed record KnobInfo(Knob Knob, int Default, int[] Choices);

/// <summary>One setting of every <see cref="Knob"/>. Immutable; <see cref="With"/> makes the neighbour the tuner tries next.</summary>
public sealed class ExpertTuning
{
    /// <summary>Indexed by <see cref="Knob"/>.</summary>
    public static readonly KnobInfo[] Knobs =
    {
        new(Knob.Opener, 1, new[] { 0, 1 }),
        new(Knob.Style, 0, new[] { 0, 1 }),
        new(Knob.VenerationAt, 1, new[] { 1, 2, 3, 4, 6, 99 }),
        new(Knob.DurabilityRate, 40, new[] { 8, 12, 18, 24, 30, 40 }),
        new(Knob.StepCost, 0, new[] { 0, 2, 5, 10, 16, 24, 32, 48, 64 }),
        new(Knob.IqValue, 0, new[] { 0, 10, 20, 30, 45, 60, 90 }),
        new(Knob.ComboCredit, 5, new[] { 0, 5, 10, 16, 24 }),
        new(Knob.ByregotReserve, 56, new[] { 24, 42, 56, 74, 92 }),
        new(Knob.FinisherMinIq, 6, new[] { 6, 8, 10 }),
        new(Knob.ManipulationBelow, 45, new[] { 0, 30, 45, 60, 80, 100 }),
        new(Knob.PliantManipulationAt, 2, new[] { -1, 0, 1, 2, 3, 4 }),
        new(Knob.PrimedManipulation, 1, new[] { 0, 1, 50, 75 }),
        new(Knob.ImmaculateMissing, 40, new[] { 25, 30, 35, 40, 45, 999 }),
        new(Knob.TricksBelowCp, 320, new[] { 0, 60, 120, 200, 320 }),
        new(Knob.GreatStridesMinIq, 8, new[] { 4, 6, 8, 10, 11 }),
        new(Knob.InnovationSpare, 0, new[] { 0, 18, 36, 60, 90 }),
        new(Knob.ObserveForDurability, 1, new[] { 0, 1 }),
        new(Knob.TrainedPerfection, 2, new[] { 0, 1, 2 }),
        new(Knob.GoodInProgress, 1, new[] { 0, 1, 2 }),
        new(Knob.RateSlope, 12, new[] { 4, 8, 12, 16, 24, 40, 999 }),
        new(Knob.ByregotDurability, 5, new[] { 0, 5, 10 }),
        new(Knob.TricksWeight, 5, new[] { 0, 5, 10, 15, 20, 30, 45 }),
        new(Knob.Aim, 100, new[] { 85, 90, 95, 100 }),
        new(Knob.Reach, 0, new[] { 0, 5, 10, 15, 20, 30 }),
        new(Knob.SteadyHand, 3, new[] { 0, 1, 2, 3 }),
        new(Knob.Miracle, 1, new[] { 0, 1, 2, 3 }),
        new(Knob.DutyUses, 1, new[] { 0, 1, 2, 3 }),
        new(Knob.WaitForGood, 0, new[] { 0, 1 }),
        new(Knob.Strides, 0, new[] { 0, 4, 6, 8, 10 }),
        new(Knob.OmenStrides, 0, new[] { 0, 1, 2 }),
        new(Knob.NetOn, 0, new[] { 0, 1, 2, 3 }),
        new(Knob.Thrift, 0, new[] { 0, 1 }),
        new(Knob.Rapid, 0, new[] { 0, 1 }),
        new(Knob.WasteNot, 0, new[] { 0, 1, 2 }),
        new(Knob.Delicate, 0, new[] { 0, 50, 100, 150 }),
        new(Knob.Mend, 0, new[] { 0, 1 }),
        new(Knob.Perfection, 0, new[] { 0, 1 }),
        new(Knob.PliantMend, 0, new[] { 0, 1 }),
        new(Knob.SturdyInnovation, 1, new[] { 0, 1, 2 }),
        new(Knob.ObserveCombo, 0, new[] { 0, 1 }),
        new(Knob.Prep, 0, new[] { 0, 1, 2, 3 }),
        new(Knob.MendBeforeFinesse, 0, new[] { 0, 1 }),
        new(Knob.HeartAndSoul, 3, new[] { 0, 1, 2, 3 }),
        new(Knob.QuickInnovation, 1, new[] { 0, 1 }),
    };

    /// <summary>
    /// The defaults are not guesses: they are what <see cref="ExpertTuner"/> settled on for the three expert recipes
    /// of the benchmark together (2026-11-11, 3,000 crafts per recipe per setting). Retune and move them when the
    /// policy gains a rule or the benchmark gains a recipe - a default nobody measured is a bug waiting.
    /// </summary>
    public static readonly ExpertTuning Default = new(Knobs.Select(k => k.Default).ToArray());

    private readonly int[] _values;

    private ExpertTuning(int[] values) => _values = values;

    public int this[Knob knob] => _values[(int)knob];

    public ExpertTuning With(Knob knob, int value)
    {
        var values = (int[])_values.Clone();
        values[(int)knob] = value;
        return new ExpertTuning(values);
    }

    /// <summary>"Knob=value, Knob=value" laid over <see cref="Default"/> - the form <see cref="Changes"/> writes.</summary>
    public static ExpertTuning Parse(string? text)
    {
        var tuning = Default;
        if (text is null || text.Trim() == "defaults")
            return tuning;

        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = pair.Split('=');
            tuning = tuning.With(Enum.Parse<Knob>(parts[0].Trim()), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        return tuning;
    }

    /// <summary>Only what differs from <see cref="Default"/>; "defaults" when nothing does.</summary>
    public string Changes()
    {
        var changed = Knobs.Where(k => this[k.Knob] != k.Default).Select(k => $"{k.Knob}={this[k.Knob]}").ToList();
        return changed.Count == 0 ? "defaults" : string.Join(", ", changed);
    }

    public override string ToString() => string.Join(", ", Knobs.Select(k => $"{k.Knob}={this[k.Knob]}"));
}
