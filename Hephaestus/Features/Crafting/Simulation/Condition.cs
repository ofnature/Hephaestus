using System;

namespace Hephaestus.Features.Crafting.Simulation;

/// <summary>Material condition. Values match the game's (Synthesis AtkValue 12), so a read casts straight in.</summary>
public enum Condition : byte
{
    Normal = 0,

    /// <summary>Quality x1.5 (x1.75 with Splendorous tools). Enables Tricks / Precise Touch / Intensive Synthesis.</summary>
    Good = 1,

    /// <summary>Quality x4. Always followed by Poor.</summary>
    Excellent = 2,

    /// <summary>Quality x0.5.</summary>
    Poor = 3,

    /// <summary>Expert: +25% success rate.</summary>
    Centered = 4,

    /// <summary>Expert: durability cost halved.</summary>
    Sturdy = 5,

    /// <summary>Expert: CP cost halved.</summary>
    Pliant = 6,

    /// <summary>Expert: progress x1.5.</summary>
    Malleable = 7,

    /// <summary>Expert: new buffs last 2 turns longer.</summary>
    Primed = 8,

    /// <summary>Expert: the next condition is Good.</summary>
    GoodOmen = 9,

    /// <summary>Cosmic: durability cost halved; the next condition is Sturdy.</summary>
    Robust = 10,
}

/// <summary>
/// How conditions follow each other. A recipe's RecipeLevelTable.ConditionsFlag says which
/// conditions can appear (bit N = <see cref="Condition"/> N); this turns that into odds.
/// </summary>
public static class ConditionOdds
{
    public const int ConditionCount = 11;

    /// <summary>Normal | Good | Excellent | Poor — every non-expert recipe.</summary>
    public const ushort NormalRecipeFlags = 0b1111;

    /// <summary>Anything beyond the basic four makes the recipe behave as expert (Good no longer forces Normal).</summary>
    public static bool IsExpert(ushort conditionsFlag) => (conditionsFlag & ~NormalRecipeFlags) != 0;

    /// <summary>
    /// Odds of each condition on a free roll, indexed by <see cref="Condition"/>; sums to 1.
    /// Expert tables are Artisan's measurements (10,000+ steps per set, CraftingLogic/State.cs);
    /// an unmeasured set falls back to 12% per special condition.
    /// </summary>
    public static float[] ForRecipe(ushort conditionsFlag, int jobLevel)
    {
        var odds = new float[ConditionCount];

        if (!IsExpert(conditionsFlag))
        {
            odds[(int)Condition.Good] = jobLevel >= 63 ? 0.25f : 0.20f;
            odds[(int)Condition.Excellent] = 0.04f;
        }
        else if (Measured(conditionsFlag) is { } measured)
        {
            measured.CopyTo(odds, 0);
        }
        else
        {
            for (var i = (int)Condition.Good; i < ConditionCount; i++)
            {
                if (i is (int)Condition.Excellent or (int)Condition.Poor)
                    continue;
                if ((conditionsFlag & (1 << i)) != 0)
                    odds[i] = 0.12f;
            }
        }

        // Normal takes whatever is left, which also absorbs rounding in the measured tables.
        odds[(int)Condition.Normal] = 0f;
        var others = 0f;
        foreach (var value in odds)
            others += value;
        odds[(int)Condition.Normal] = Math.Max(0f, 1f - others);
        return odds;
    }

    /// <summary>
    /// The condition after <paramref name="current"/>. Forced transitions ignore the roll:
    /// Excellent→Poor, Poor→Normal, Good Omen→Good, Robust→Sturdy, and Good→Normal on non-expert recipes.
    /// </summary>
    /// <param name="rollOdds">From <see cref="ForRecipe"/>.</param>
    /// <param name="roll">Uniform in [0, 1).</param>
    /// <param name="materialMiracle">
    /// Cosmic Exploration: Material Miracle is running (<see cref="CraftState.MaterialMiracle"/> above 0 AFTER
    /// the action). A free roll then lands on one of its six conditions, whatever the recipe deals - evenly,
    /// by what Artisan measured. Forced transitions still come first.
    /// </param>
    public static Condition Next(float[] rollOdds, bool expert, Condition current, float roll, bool materialMiracle)
    {
        if (!materialMiracle || current is Condition.Excellent or Condition.Poor or Condition.GoodOmen or Condition.Robust
            || (current == Condition.Good && !expert))
        {
            return Next(rollOdds, expert, current, roll);
        }

        return MiracleConditions[Math.Min(MiracleConditions.Length - 1, (int)(roll * MiracleConditions.Length))];
    }

    private static readonly Condition[] MiracleConditions =
    {
        Condition.Good, Condition.Centered, Condition.Sturdy, Condition.Pliant, Condition.Malleable, Condition.Primed,
    };

    /// <summary>The same with no Material Miracle running.</summary>
    public static Condition Next(float[] rollOdds, bool expert, Condition current, float roll)
    {
        switch (current)
        {
            case Condition.Excellent: return Condition.Poor;
            case Condition.Poor: return Condition.Normal;
            case Condition.GoodOmen: return Condition.Good;
            case Condition.Robust: return Condition.Sturdy;
            case Condition.Good when !expert: return Condition.Normal;
        }

        for (var i = 1; i < rollOdds.Length; i++)
        {
            roll -= rollOdds[i];
            if (roll < 0f)
                return (Condition)i;
        }

        return Condition.Normal;
    }

    // Indexed by Condition: Normal, Good, Excellent, Poor, Centered, Sturdy, Pliant, Malleable, Primed, GoodOmen, Robust.
    private static float[]? Measured(ushort flags) => flags switch
    {
        115 => new[] { 0.46f, 0.12f, 0, 0, 0.15f, 0.15f, 0.12f, 0, 0, 0, 0 },
        483 => new[] { 0.37f, 0.12f, 0, 0, 0, 0.15f, 0.12f, 0.12f, 0.12f, 0, 0 },
        499 => new[] { 0.30f, 0.12f, 0, 0, 0.13f, 0.10f, 0.10f, 0.10f, 0.15f, 0, 0 },
        435 => new[] { 0.40f, 0.12f, 0, 0, 0.12f, 0.12f, 0, 0.12f, 0.12f, 0, 0 },
        995 => new[] { 0.32f, 0.04f, 0, 0, 0, 0.15f, 0.10f, 0.12f, 0.15f, 0.12f, 0 },
        627 => new[] { 0.42f, 0.04f, 0, 0, 0.15f, 0.15f, 0.12f, 0, 0, 0.12f, 0 },
        1011 => new[] { 0.22f, 0.12f, 0, 0, 0.12f, 0.12f, 0.10f, 0.10f, 0.12f, 0.10f, 0 },
        19 => new[] { 0.48f, 0.12f, 0, 0, 0.40f, 0, 0, 0, 0, 0, 0 },
        67 => new[] { 0.48f, 0.12f, 0, 0, 0, 0, 0.40f, 0, 0, 0, 0 },
        35 => new[] { 0.48f, 0.12f, 0, 0, 0, 0.40f, 0, 0, 0, 0, 0 },
        515 => new[] { 0.68f, 0.02f, 0, 0, 0, 0, 0, 0, 0, 0.30f, 0 },
        259 => new[] { 0.48f, 0.12f, 0, 0, 0, 0, 0, 0, 0.40f, 0, 0 },
        131 => new[] { 0.48f, 0.12f, 0, 0, 0, 0, 0, 0.40f, 0, 0, 0 },
        1363 => new[] { 0.45f, 0.12f, 0, 0, 0.13f, 0, 0.10f, 0, 0.12f, 0, 0.08f },
        1491 => new[] { 0.25f, 0.17f, 0, 0, 0.12f, 0, 0.11f, 0.15f, 0.10f, 0, 0.10f },
        243 => new[] { 0.30f, 0.12f, 0, 0, 0.19f, 0.12f, 0.15f, 0.12f, 0, 0, 0 },
        1459 => new[] { 0.32f, 0.12f, 0, 0, 0.15f, 0.10f, 0, 0.12f, 0.14f, 0, 0.05f },
        1523 => new[] { 0.20f, 0.12f, 0, 0, 0.15f, 0.08f, 0.15f, 0.10f, 0.10f, 0, 0.10f },
        _ => null,
    };
}
