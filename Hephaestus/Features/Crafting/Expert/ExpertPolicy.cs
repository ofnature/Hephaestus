using System;
using Hephaestus.Features.Crafting.Simulation;
using static Hephaestus.Features.Crafting.Simulation.CraftAction;

namespace Hephaestus.Features.Crafting.Expert;

/// <summary>
/// Hephaestus's OWN policy for recipes a fixed rotation cannot carry
/// (expert recipes, Cosmic Exploration's hard ones): given the craft as it stands and the condition
/// of the material, the one action to press. No plan, no memory - everything it decides by is in the
/// state, so it can be asked from anywhere, a million times a second: it is both a player in its own
/// right and what <see cref="ExpertSearch"/> plays its sampled futures with.
///
/// Written from how the game works, not from anyone's code:
///  - progress comes from Rapid Synthesis (free, half the time nothing) and Intensive Synthesis on
///    Good, and stops ONE sure action short of the end - the item is closed out last;
///  - durability is a currency with a price in CP (<see cref="Knob.DurabilityRate"/>); every touch is
///    judged by what it yields per CP spent, durability included, UNDER THE CONDITION IT IS TAKEN -
///    which is how a Sturdy, Pliant, Centered or Good step gets used for what it is good for;
///  - the last of the CP belongs to Byregot's Blessing, which ends the quality work.
/// Every threshold is a <see cref="Knob"/>, so the tuner can turn it and the simulations can judge.
/// </summary>
public static class ExpertPolicy
{
    private static readonly CraftAction[] Finishers = { BasicSynthesis, IntensiveSynthesis, CarefulSynthesis, PrudentSynthesis, Groundwork };
    private static readonly CraftAction[] Progressors = { RapidSynthesis, IntensiveSynthesis, Groundwork, CarefulSynthesis, PrudentSynthesis, BasicSynthesis };

    private static readonly CraftAction[] Touches =
    {
        DaringTouch, AdvancedTouch, StandardTouch, RefinedTouch, BasicTouch, PrudentTouch, PreparatoryTouch, HastyTouch, PreciseTouch, TrainedFinesse,
        DelicateSynthesis,
    };

    /// <param name="target">The quality the item is worth nothing below (the recipe's demand, else its full quality).</param>
    /// <param name="tiers">
    /// A Cosmic mission collectable's three ratings as quality, lowest first: the item is then worth points at each
    /// of them and for every percent above the last, so the aim is <see cref="Knob.Aim"/> and not <paramref name="target"/>.
    /// </param>
    /// <returns>Always a legal action while the craft is not over.</returns>
    public static CraftAction Decide(
        in CraftParameters parameters, ExpertTuning tuning, int target, in CraftState state, Condition condition, int[]? tiers = null)
        => new Turn(parameters, tuning, target, state, condition, tiers).Decide();

    private readonly ref struct Turn
    {
        private readonly CraftParameters P;
        private readonly ExpertTuning T;
        private readonly CraftState S;
        private readonly Condition C;

        /// <summary>Progress still missing.</summary>
        private readonly int Remaining;

        /// <summary>Quality still missing; zero or less = the item is worth having.</summary>
        private readonly int QualityLeft;

        /// <summary>What one plain Careful Synthesis adds: the craft is "one step from done" within this.</summary>
        private readonly int FinishGain;

        /// <summary>What one plain Rapid Synthesis adds when it lands.</summary>
        private readonly int PlainRapid;

        /// <summary>A scored collectable's ratings; null for an item with one demand.</summary>
        private readonly int[]? Tiers;

        public Turn(in CraftParameters parameters, ExpertTuning tuning, int target, in CraftState state, Condition condition, int[]? tiers)
        {
            P = parameters;
            T = tuning;
            S = state;
            C = condition;
            Tiers = tiers;
            Unperfected = state;
            Unperfected.TrainedPerfectionActive = false;
            if (tiers != null)
                target = Math.Max(tiers[^1], (int)((long)parameters.MaxQuality * tuning[Knob.Aim] / 100));

            Remaining = parameters.MaxProgress - state.Progress;
            QualityLeft = target - state.Quality;

            var plain = new CraftState { Durability = parameters.MaxDurability };
            FinishGain = CraftSimulator.ProgressGain(parameters, plain, CarefulSynthesis, Condition.Normal);
            PlainRapid = CraftSimulator.ProgressGain(parameters, plain, RapidSynthesis, Condition.Normal);

            // How scarce CP is: what the rest of the craft needs at a plain touch's rate, over what is in hand. An
            // ordinary recipe has CP to burn - there the step is what costs, and the bigger action should win.
            Scarcity = 1.0;
            if (tuning[Knob.Thrift] == 1)
            {
                var typical = state;
                typical.InnerQuiet = Math.Max(state.InnerQuiet, (byte)5);
                typical.Innovation = 0;
                typical.GreatStrides = 0;
                var perCp = Math.Max(1.0, CraftSimulator.QualityGain(parameters, typical, BasicTouch, Condition.Normal) / 18.0);
                var need = Math.Max(0, QualityLeft) / perCp + 7.0 * Math.Ceiling(Remaining / (double)Math.Max(1, FinishGain));
                Scarcity = Math.Clamp(need / Math.Max(1, state.Cp), 0.15, 1.0);
            }

            // Durability can always be bought at Manipulation's price, so it is never worth more than the cap; it is
            // worth less the scarcer CP is beside it - nothing turns durability back into CP.
            Rate = tuning[Knob.DurabilityRate] / 10.0;
            if (tuning[Knob.RateSlope] < 999)
            {
                var spare = Math.Max(0, state.Cp - 7 - tuning[Knob.ByregotReserve]);
                var durability = Math.Max(5, state.Durability + 5 * state.Manipulation);
                Rate = Math.Min(Rate, tuning[Knob.RateSlope] / 10.0 * spare / durability);
            }
        }

        /// <summary>What a point of durability is worth in CP, here and now.</summary>
        private readonly double Rate;

        /// <summary>1 = every CP is needed; towards 0 = far more CP in hand than the craft will use (<see cref="Knob.Thrift"/>).</summary>
        private readonly double Scarcity;

        /// <summary>The craft as it stands, without the Trained Perfection that may be waiting: what an action would wear otherwise.</summary>
        private readonly CraftState Unperfected;

        /// <summary>What an action costs in CP, durability included, at what CP is worth right now.</summary>
        private double Price(CraftAction action)
        {
            double wear = Wear(action);

            // Trained Perfection is waiting and this action would take it: what it costs is the durability the free
            // action could have saved and now will not - twenty at most (Groundwork, Preparatory Touch).
            if (S.TrainedPerfectionActive && T[Knob.Perfection] == 1)
            {
                var otherwise = CraftSimulator.DurabilityCost(Unperfected, action, C);
                if (otherwise > 0)
                    wear = Math.Max(0, 20 - otherwise);
            }

            return (Cp(action) + Rate * wear) * Scarcity;
        }

        private bool Can(CraftAction action) => CraftSimulator.Check(P, S, action, C) == ActionError.None;

        private int Cp(CraftAction action) => CraftSimulator.CpCost(S, action, C);

        private int Wear(CraftAction action) => CraftSimulator.DurabilityCost(S, action, C);

        private int Progress(CraftAction action) => CraftSimulator.ProgressGain(P, S, action, C);

        private int Quality(CraftAction action) => CraftSimulator.QualityGain(P, S, action, C);

        private bool Affordable(CraftAction action, int keep) => Can(action) && S.Cp - Cp(action) >= keep;

        /// <summary>
        /// The item survives the action, and enough durability is left (or about to be mended back by
        /// Manipulation, three steps of it at most) for <paramref name="keep"/> more.
        /// </summary>
        private bool Durable(CraftAction action, int keep)
        {
            var wear = Wear(action);
            if (wear == 0)
                return true;

            var left = S.Durability - wear;
            return left >= 5 && left + 5 * Math.Min((int)S.Manipulation, 3) >= 5 + keep;
        }

        /// <summary>CP that completing the item will still take: the closing synthesis, plus the Rapid Synthesis attempts before it, at durability's price.</summary>
        private int FinishCp
        {
            get
            {
                var beyond = Remaining - FinishGain;
                return beyond <= 0 ? 7 : 7 + (int)(Math.Ceiling(beyond / (PlainRapid * 0.5)) * 10 * Rate);
            }
        }

        public CraftAction Decide()
        {
            // Material Miracle is instant: cast before the opener, the opener is still the first step.
            if (QualityLeft > 0 && Miracle() is { } miracle)
                return miracle;

            if (S.Combo == Combo.SynthesisBegin)
            {
                var opener = T[Knob.Opener] == 1 ? Reflect : MuscleMemory;
                if (Can(opener) && (opener == Reflect || Progress(opener) < Remaining))
                    return opener;
            }

            if (QualityLeft <= 0 || OutOfReach())
                return Finish();

            if (Soul() is { } soul)
                return soul;

            if (WasteNotDue() is { } wasteNot)
                return wasteNot;

            // A free durability action, taken on a step that offers nothing better.
            if (T[Knob.TrainedPerfection] == 2 && C is Condition.Normal or Condition.Primed or Condition.GoodOmen
                && S.MuscleMemory == 0 && S.SteadyHand == 0 && Can(TrainedPerfection))
            {
                return TrainedPerfection;
            }

            if (Remaining > FinishGain && WantsProgress() && ProgressStep() is { } progress)
                return progress;

            return QualityStep() ?? Finish();
        }

        /// <summary>
        /// Heart and Soul (a specialist's, once a craft, no step taken): pressed for the action it unlocks, and that
        /// action pressed while it waits - on a step that is not Good anyway, where the three are open for free.
        /// </summary>
        private CraftAction? Soul()
        {
            var mode = T[Knob.HeartAndSoul];
            if (C is Condition.Good or Condition.Excellent || (!S.HeartAndSoulActive && (mode == 0 || !S.HeartAndSoulAvailable)))
                return null;

            var progressWanted = Remaining > FinishGain && WantsProgress() && Progress(IntensiveSynthesis) < Remaining;
            var keep = FinishCp + T[Knob.ByregotReserve];

            if (S.HeartAndSoulActive)
            {
                // It is up (ours, or the search's): Intensive Synthesis while progress is the work in hand, else Precise Touch.
                if (progressWanted && (mode & 1) != 0 && Can(IntensiveSynthesis) && S.Durability - Wear(IntensiveSynthesis) >= 5)
                    return IntensiveSynthesis;
                if (Affordable(PreciseTouch, FinishCp) && Durable(PreciseTouch, 0))
                    return PreciseTouch;
                return progressWanted && Can(IntensiveSynthesis) && S.Durability - Wear(IntensiveSynthesis) >= 5 ? IntensiveSynthesis : null;
            }

            if (mode == 0 || !Can(HeartAndSoul))
                return null;

            if ((mode & 1) != 0 && progressWanted && (S.Veneration > 0 || S.MuscleMemory > 0) && S.Cp - 6 >= 7 && S.Durability - Wear(IntensiveSynthesis) >= 5)
                return HeartAndSoul;
            if ((mode & 2) != 0 && !progressWanted && S.Innovation > 0 && S.Cp - 18 >= keep && Durable(PreciseTouch, T[Knob.ByregotDurability]))
                return HeartAndSoul;
            return null;
        }

        /// <summary>Waste Not, while enough steps that wear the item are still to come for it to pay (<see cref="Knob.WasteNot"/>).</summary>
        private CraftAction? WasteNotDue()
        {
            var mode = T[Knob.WasteNot];
            if (mode == 0 || S.WasteNot > 0 || S.TrainedPerfectionActive || S.SteadyHand > 0)
                return null;

            // Steps of work left, counted in the biggest sure actions: Groundwork under Veneration, Preparatory Touch under Innovation.
            var boosted = S;
            boosted.Veneration = 1;
            boosted.Innovation = 1;
            boosted.Durability = P.MaxDurability;
            var perSynthesis = Math.Max(1, CraftSimulator.ProgressGain(P, boosted, Groundwork, Condition.Normal));
            var perTouch = Math.Max(1, CraftSimulator.QualityGain(P, boosted, PreparatoryTouch, Condition.Normal));
            var steps = (int)Math.Ceiling(Math.Max(0, Remaining - FinishGain) / (double)perSynthesis)
                        + (int)Math.Ceiling(Math.Max(0, QualityLeft) / (double)perTouch);

            var action = mode == 2 ? WasteNot2 : WasteNot;
            return steps >= (mode == 2 ? 6 : 3) && Affordable(action, FinishCp + T[Knob.ByregotReserve]) ? action : null;
        }

        /// <summary>Material Miracle, when the mission lent a use, none is running, and the moment <see cref="Knob.Miracle"/> names has come.</summary>
        private CraftAction? Miracle()
        {
            var due = T[Knob.Miracle] switch
            {
                1 => true,
                2 => Remaining <= FinishGain,
                3 => S.InnerQuiet >= 8,
                _ => false,
            };
            return due && Can(MaterialMiracle) ? MaterialMiracle : null;
        }

        /// <summary>
        /// A scored collectable only: the next rating cannot be reached with the CP in hand, so more quality work is
        /// time spent for no points. Above the top rating every percent counts, and the work goes on.
        /// </summary>
        private bool OutOfReach()
        {
            if (Tiers is null || T[Knob.Reach] == 0)
                return false;

            var next = 0;
            foreach (var tier in Tiers)
            {
                if (S.Quality < tier)
                {
                    next = tier;
                    break;
                }
            }

            if (next == 0)
                return false;

            var boosted = S;
            boosted.Innovation = 1;
            boosted.GreatStrides = 0;
            var perCp = CraftSimulator.QualityGain(P, boosted, AdvancedTouch, Condition.Normal) / 18.0;
            var spare = Math.Max(0, S.Cp - FinishCp);
            return S.Quality + spare * perCp * T[Knob.Reach] / 10.0 < next;
        }

        /// <summary>Progress first (style 0), or only while something makes this step a good one for it (style 1).</summary>
        private bool WantsProgress()
            => T[Knob.Style] == 0
               || S.MuscleMemory > 0
               || S.Veneration > 0
               || C is Condition.Centered or Condition.Malleable or Condition.Sturdy or Condition.Robust or Condition.Good or Condition.Excellent;

        private CraftAction? ProgressStep()
        {
            // Steps that cannot fail are for the action that can: nothing else is pressed while Stellar Steady Hand is up.
            if (S.SteadyHand > 0)
                return BestProgress(finishing: false, keep: 7);

            // Progress is the cheap half of an expert craft; a Good step may be worth more spent on CP or on quality.
            if (C is Condition.Good or Condition.Excellent)
            {
                var use = T[Knob.GoodInProgress];
                if (use == 2 && Affordable(PreciseTouch, 7 + T[Knob.ByregotReserve]) && Durable(PreciseTouch, 0))
                    return PreciseTouch;
                if (use != 0 && S.Cp < P.MaxCp && Can(TricksOfTheTrade))
                    return TricksOfTheTrade;
            }

            if (S.MuscleMemory == 0 && Upkeep(7) is { } upkeep)
                return upkeep;

            // Veneration while there is a lot to do - but not at the cost of a step the material favours, nor of Muscle Memory's last ones.
            if (S.Veneration == 0
                && C is not (Condition.Malleable or Condition.Centered or Condition.Good or Condition.Excellent)
                && S.MuscleMemory is 0 or >= 3
                && (Remaining - FinishGain) * 2 > PlainRapid * T[Knob.VenerationAt]
                && Affordable(Veneration, 7))
            {
                return Veneration;
            }

            // Three steps that cannot fail, when more than one Rapid Synthesis of progress is still to make.
            if ((T[Knob.SteadyHand] & 1) != 0 && S.SteadyHand == 0 && Remaining - FinishGain > PlainRapid && Can(StellarSteadyHand))
                return StellarSteadyHand;

            return BestProgress(finishing: false, keep: 7);
        }

        /// <summary>The synthesis that adds the most per CP, durability included. Not finishing = stopping short of the end, with the item intact.</summary>
        private CraftAction? BestProgress(bool finishing, int keep)
        {
            CraftAction? best = null;
            var bestScore = 0.0;
            foreach (var action in Progressors)
            {
                if (!Can(action))
                    continue;

                var gain = Progress(action);
                if (gain >= Remaining)
                {
                    if (!finishing)
                        continue;
                }
                else if (S.Durability - Wear(action) < 5 || S.Cp - Cp(action) < keep)
                {
                    continue;
                }

                var odds = CraftSimulator.SuccessPercent(S, action, C);
                if (odds < 75 && T[Knob.Rapid] == 1)
                    continue;

                var score = odds / 100.0 * Math.Min(gain, Remaining) / Math.Max(1.0, Price(action) + T[Knob.StepCost]);
                if (score > bestScore)
                    (best, bestScore) = (action, score);
            }

            return best;
        }

        private CraftAction? QualityStep()
        {
            var finishCp = FinishCp;
            var keep = finishCp + T[Knob.ByregotReserve];

            // The close: Byregot's Blessing as soon as it reaches the demand - with the buffs it needs, if it needs them.
            var byregotInReach = S.InnerQuiet > 0 && Can(ByregotsBlessing) && S.Cp - Cp(ByregotsBlessing) >= finishCp && Durable(ByregotsBlessing, 0);
            if (byregotInReach)
            {
                if (Quality(ByregotsBlessing) >= QualityLeft)
                    return ByregotsBlessing;
                if (QuickClose())
                    return QuickInnovation;
                if (S.InnerQuiet >= T[Knob.FinisherMinIq] && FinisherSetup(finishCp) is { } setup)
                    return setup;
            }

            if (S.SteadyHand > 0 && BestTouch(keep, T[Knob.ByregotDurability], out _) is { } sure)
                return sure;

            // The second half of Observe > Advanced Touch (or the third of Basic > Standard > Advanced): nothing comes between.
            if (T[Knob.ObserveCombo] == 1 && S.Combo == Combo.StandardTouch && C is not (Condition.Good or Condition.Excellent)
                && Affordable(AdvancedTouch, keep) && Durable(AdvancedTouch, T[Knob.ByregotDurability]))
            {
                return AdvancedTouch;
            }

            // A Good step is used before anything else is seen to: for a touch, or for CP when the touch nets less than the CP would.
            var good = C is Condition.Good or Condition.Excellent;
            if (good && Can(TricksOfTheTrade) && S.Cp < P.MaxCp)
            {
                if (S.Cp < T[Knob.TricksBelowCp])
                    return TricksOfTheTrade;
                if (Favoured(keep) is { } big)
                    return big;
                if (BestTouch(keep, T[Knob.ByregotDurability], out var net) is not { } goodTouch)
                    return TricksOfTheTrade;

                var perCp = CraftSimulator.QualityGain(P, S, BasicTouch, Condition.Normal) / 32.0;
                return net < 20 * perCp * T[Knob.TricksWeight] / 10.0 ? TricksOfTheTrade : goodTouch;
            }

            if (good && Favoured(keep) is { } bigGood)
                return bigGood;

            // A Good step is coming and Inner Quiet is as good as full: Great Strides for it, before anything else.
            if (T[Knob.OmenStrides] == 2 && C == Condition.GoodOmen && S.GreatStrides == 0 && S.InnerQuiet >= 9
                && Affordable(GreatStrides, keep + 18) && S.Durability >= 15)
            {
                return GreatStrides;
            }

            if (Upkeep(keep) is { } upkeep)
                return upkeep;

            if (!good && Favoured(keep) is { } favoured)
                return favoured;

            // A step that wears the item half as much is for wearing it: the touch now, Innovation after.
            var forTouch = T[Knob.SturdyInnovation] switch
            {
                0 => C is Condition.Sturdy or Condition.Robust,
                2 => C is Condition.Sturdy or Condition.Robust or Condition.Centered,
                _ => false,
            };
            if (forTouch && S.Innovation == 0 && BestTouch(keep, T[Knob.ByregotDurability], out _) is { } sturdy)
                return sturdy;


            if (T[Knob.TrainedPerfection] == 1 && !good && S.Innovation == 0 && Can(TrainedPerfection) && S.Cp - 58 >= keep)
                return TrainedPerfection;

            if (!good && !(C == Condition.GoodOmen && T[Knob.OmenStrides] == 1 && S.GreatStrides == 0 && S.InnerQuiet >= T[Knob.GreatStridesMinIq])
                && (S.Innovation == 0 || (C == Condition.Primed && S.Innovation <= 1)) && Affordable(Innovation, keep + T[Knob.InnovationSpare]))
            {
                return Innovation;
            }

            // A Good step is coming: have Great Strides waiting for it.
            if (C == Condition.GoodOmen && S.GreatStrides == 0 && (S.Innovation >= 2 || T[Knob.OmenStrides] == 1) && S.InnerQuiet >= T[Knob.GreatStridesMinIq]
                && Affordable(GreatStrides, keep + 18) && S.Durability >= 25)
            {
                return GreatStrides;
            }

            // Great Strides for the next touch, while Innovation still covers it: the dearest touch, doubled, is the best
            // quality a step can make - which is what counts when the steps themselves are what is scarce.
            if (T[Knob.Strides] > 0 && !good && S.GreatStrides == 0 && S.Innovation >= 2 && S.InnerQuiet >= T[Knob.Strides]
                && Affordable(GreatStrides, keep + 40) && Durable(PreparatoryTouch, T[Knob.ByregotDurability]))
            {
                return GreatStrides;
            }

            // Three free touches that cannot fail (Hasty, Daring, Hasty), while there is Inner Quiet to build with them.
            if ((T[Knob.SteadyHand] & 2) != 0 && S.SteadyHand == 0 && S.InnerQuiet < 10 && Can(StellarSteadyHand)
                && Durable(HastyTouch, T[Knob.ByregotDurability] + 10))
            {
                return StellarSteadyHand;
            }

            if (BestTouch(keep, T[Knob.ByregotDurability], out _) is { } touch)
            {
                if (touch == TrainedFinesse && T[Knob.MendBeforeFinesse] == 1 && Repair(keep) is { } mend)
                    return mend;
                return touch;
            }

            // Nothing worth its cost can be pressed. Take what is free, wait for the mending, or mend.
            if (Can(TricksOfTheTrade) && S.Cp < P.MaxCp)
                return TricksOfTheTrade;
            if (T[Knob.ObserveForDurability] == 1 && S.Manipulation > 0 && S.Durability < P.MaxDurability && Affordable(Observe, keep))
                return Observe;
            if (Repair(keep) is { } repair)
                return repair;

            // Out of means: the reserve goes into Byregot's Blessing, buffed as far as the CP reaches.
            if (byregotInReach)
            {
                var spare = S.Cp - finishCp - CraftSimulator.CpCost(S, ByregotsBlessing, Condition.Normal);
                if (S.GreatStrides == 0)
                {
                    if (S.Innovation < 2 && spare >= 50)
                        return Innovation;
                    if (spare >= 32)
                        return GreatStrides;
                }
                else if (S.Innovation == 0 && S.GreatStrides >= 2 && spare >= 18)
                {
                    return Innovation;
                }

                // The next step is Good: the blessing is worth half as much again there, if the buffs last that long.
                if (T[Knob.WaitForGood] == 1 && C == Condition.GoodOmen && spare >= 7 && S.GreatStrides != 1 && S.Innovation != 1 && Can(Observe))
                    return Observe;

                // The last thing before the blessing, so that nothing ticks it away: a free Innovation that takes no step.
                if (T[Knob.QuickInnovation] == 1 && S.Innovation == 0 && Can(QuickInnovation))
                    return QuickInnovation;

                return ByregotsBlessing;
            }

            return null;
        }

        /// <summary>Quick Innovation is all the closing Byregot's Blessing lacks to reach the demand.</summary>
        private bool QuickClose()
        {
            if (T[Knob.QuickInnovation] != 1 || S.Innovation != 0 || !Can(QuickInnovation))
                return false;

            var boosted = S;
            boosted.Innovation = 1;
            return CraftSimulator.QualityGain(P, boosted, ByregotsBlessing, C) >= QualityLeft;
        }

        /// <summary>Preparatory Touch on a step the material favours, with Inner Quiet full and a buff over it (<see cref="Knob.Prep"/>).</summary>
        private CraftAction? Favoured(int keep)
        {
            if (S.InnerQuiet < 10 || (S.Innovation == 0 && S.GreatStrides == 0))
                return null;

            var named = T[Knob.Prep] switch
            {
                1 => C is Condition.Good or Condition.Excellent,
                2 => C is Condition.Good or Condition.Excellent or Condition.Sturdy or Condition.Robust,
                3 => C is Condition.Good or Condition.Excellent or Condition.Sturdy or Condition.Robust or Condition.Pliant,
                _ => false,
            };
            return named && Affordable(PreparatoryTouch, keep) && Durable(PreparatoryTouch, T[Knob.ByregotDurability]) ? PreparatoryTouch : null;
        }

        /// <summary>The cheapest of Innovation, Great Strides or both under which Byregot's Blessing reaches the demand - its next step, if the CP is there.</summary>
        private CraftAction? FinisherSetup(int finishCp)
        {
            for (var plan = 1; plan <= 3; plan++) // 1 = Innovation, 2 = Great Strides, 3 = both
            {
                var innovation = (plan & 1) != 0;
                var strides = (plan & 2) != 0;
                var castStrides = strides && S.GreatStrides == 0;
                var castInnovation = innovation && (S.Innovation == 0 || (castStrides && S.Innovation < 2));
                var steps = (castInnovation ? 1 : 0) + (castStrides ? 1 : 0);
                if (steps == 0)
                    continue; // nothing to set up: the plain check above has already said no

                var boosted = S;
                boosted.Innovation = (byte)(castInnovation || S.Innovation > steps ? 1 : 0);
                boosted.GreatStrides = (byte)(castStrides || S.GreatStrides > steps ? 1 : 0);
                if (CraftSimulator.QualityGain(P, boosted, ByregotsBlessing, Condition.Normal) < QualityLeft)
                    continue;

                var cost = (castInnovation ? 18 : 0) + (castStrides ? 32 : 0) + CraftSimulator.CpCost(S, ByregotsBlessing, Condition.Normal);
                if (S.Cp - cost < finishCp)
                    continue;

                return castInnovation ? Innovation : GreatStrides;
            }

            return null;
        }

        /// <summary>The touch that yields the most per CP - quality now, plus what the Inner Quiet it builds is worth later.</summary>
        /// <param name="net">What the chosen touch yields beyond what its cost would have yielded spent on plain touches.</param>
        private CraftAction? BestTouch(int keep, int keepDurability, out double net)
        {
            net = 0;
            var perCp = CraftSimulator.QualityGain(P, S, BasicTouch, Condition.Normal) / 32.0;
            CraftAction? best = null;
            var bestScore = 0.0;
            var stackValue = P.BaseQuality * T[Knob.IqValue] / 10.0;

            // A doubled touch, or a step the material favours, comes once: there the touch that NETS the most is the one
            // to take, not the cheapest per CP.
            var byNet = S.GreatStrides > 0 || T[Knob.NetOn] switch
            {
                1 => C is Condition.Good or Condition.Excellent,
                2 => C is Condition.Good or Condition.Excellent or Condition.Sturdy or Condition.Robust,
                3 => true,
                _ => false,
            };
            foreach (var action in Touches)
            {
                if (!Can(action) || S.Cp - Cp(action) < keep || !Durable(action, keepDurability))
                    continue;

                var stacks = Math.Min(10 - S.InnerQuiet, action is PreciseTouch or PreparatoryTouch or RefinedTouch ? 2 : 1);
                var value = CraftSimulator.SuccessPercent(S, action, C) / 100.0 * (Quality(action) + stacks * stackValue);
                if (action == DelicateSynthesis)
                {
                    // Half a synthesis, half a touch: only while its progress is wanted and does not end the craft.
                    var progress = Progress(action);
                    if (T[Knob.Delicate] == 0 || Remaining <= FinishGain || progress >= Remaining)
                        continue;
                    value += progress / (double)Math.Max(1, P.BaseProgress) * P.BaseQuality * T[Knob.Delicate] / 100.0;
                }

                var credit = action == BasicTouch || (action == StandardTouch && S.Combo == Combo.BasicTouch) ? T[Knob.ComboCredit]
                    : action == HastyTouch ? T[Knob.ComboCredit] / 2
                    : 0;
                var cost = Math.Max(1.0, Price(action) + T[Knob.StepCost] - credit);
                var score = byNet ? value - cost * perCp : value / cost;
                if (best is null || score > bestScore)
                    (best, bestScore, net) = (action, score, value - cost * perCp);
            }

            // Observe, then Advanced Touch at its combo price: a sure touch for 25 CP and two steps. Not on a step that
            // offers something (it would be spent looking), nor while every step is one that cannot fail.
            if (T[Knob.ObserveCombo] == 1 && C is not (Condition.Good or Condition.Excellent or Condition.Pliant or Condition.Centered)
                && S.SteadyHand == 0 && S.Combo != Combo.StandardTouch && Can(Observe))
            {
                var after = S;
                after.Combo = Combo.StandardTouch;
                after.Innovation = (byte)Math.Max(0, S.Innovation - 1);
                after.GreatStrides = (byte)Math.Max(0, S.GreatStrides - 1);
                var cp = Cp(Observe) + CraftSimulator.CpCost(after, AdvancedTouch, Condition.Normal);
                var wear = CraftSimulator.DurabilityCost(after, AdvancedTouch, Condition.Normal);
                var left = S.Durability + (S.Manipulation > 0 ? 5 : 0) - wear;
                if (S.Cp - cp >= keep && left >= 5 && left + 5 * Math.Min(Math.Max(0, S.Manipulation - 1), 3) >= 5 + keepDurability)
                {
                    var value = CraftSimulator.QualityGain(P, after, AdvancedTouch, Condition.Normal) + Math.Min(10 - S.InnerQuiet, 1) * stackValue;
                    var cost = Math.Max(1.0, (cp + Rate * wear) * Scarcity + 2 * T[Knob.StepCost]);
                    var score = byNet ? value - cost * perCp : value / cost;
                    if (best is null || score > bestScore)
                        (best, bestScore, net) = (Observe, score, value - cost * perCp);
                }
            }

            return best;
        }

        /// <summary>Durability looked after BEFORE it runs out: Manipulation when the material makes it cheap or long, or when it is simply due.</summary>
        private CraftAction? Upkeep(int keep)
        {
            if (C == Condition.Pliant && T[Knob.PliantMend] == 1 && P.MaxDurability - S.Durability >= T[Knob.ImmaculateMissing] && Affordable(ImmaculateMend, keep))
                return ImmaculateMend;
            if (C == Condition.Pliant && S.Manipulation <= T[Knob.PliantManipulationAt] && Affordable(Manipulation, keep))
                return Manipulation;

            var primed = T[Knob.PrimedManipulation];
            if (C == Condition.Primed && primed != 0 && S.Manipulation == 0 && (primed == 1 || S.Durability * 100 <= P.MaxDurability * primed)
                && Affordable(Manipulation, keep))
            {
                return Manipulation;
            }
            if (S.Manipulation == 0 && S.Durability * 100 <= P.MaxDurability * T[Knob.ManipulationBelow] && Affordable(Manipulation, keep))
                return Manipulation;
            if (C == Condition.Pliant && P.MaxDurability - S.Durability >= T[Knob.ImmaculateMissing] && Affordable(ImmaculateMend, keep))
                return ImmaculateMend;
            return null;
        }

        /// <summary>Durability bought back because nothing else can be done without it.</summary>
        private CraftAction? Repair(int keep)
        {
            var missing = P.MaxDurability - S.Durability;
            if (T[Knob.Mend] == 1 && missing >= T[Knob.ImmaculateMissing] && Affordable(ImmaculateMend, keep))
                return ImmaculateMend;
            if (S.Manipulation == 0 && missing >= 10 && Affordable(Manipulation, keep))
                return Manipulation;
            if (missing >= T[Knob.ImmaculateMissing] && Affordable(ImmaculateMend, keep))
                return ImmaculateMend;
            if (missing >= 30 && Affordable(MasterMend, keep))
                return MasterMend;
            return null;
        }

        /// <summary>Complete the item: at once if one sure action does it, else by the best road there.</summary>
        private CraftAction Finish()
        {
            foreach (var action in Finishers)
            {
                if (Can(action) && Progress(action) >= Remaining)
                    return action;
            }

            if (S.Veneration == 0 && Remaining > 2 * FinishGain && Affordable(Veneration, 7))
                return Veneration;
            if (BestProgress(finishing: true, keep: 0) is { } step)
                return step;
            if (Repair(0) is { } repair)
                return repair;
            if (S.Manipulation > 0 && Can(Observe))
                return Observe;

            // Nothing safe is left: the biggest hit there is, whatever it does to the item.
            return Can(RapidSynthesis) ? RapidSynthesis : BasicSynthesis;
        }
    }
}
