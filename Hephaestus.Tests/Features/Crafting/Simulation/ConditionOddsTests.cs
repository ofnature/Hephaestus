using Hephaestus.Features.Crafting.Simulation;

namespace Hephaestus.Tests.Features.Crafting.Simulation;

public class ConditionOddsTests
{
    private const ushort ExpertFlags = 499; // Normal, Good, Centered, Sturdy, Pliant, Malleable, Primed

    [Fact]
    public void NormalRecipe_GoodAndExcellentOnly()
    {
        var odds = ConditionOdds.ForRecipe(ConditionOdds.NormalRecipeFlags, jobLevel: 100);

        Assert.Equal(0.25f, odds[(int)Condition.Good]);
        Assert.Equal(0.04f, odds[(int)Condition.Excellent]);
        Assert.Equal(0.71f, odds[(int)Condition.Normal], 3);
        Assert.Equal(0f, odds[(int)Condition.Poor]); // Poor is never rolled — it only follows Excellent
    }

    [Fact]
    public void NormalRecipe_BelowLevel63_GoodIsRarer()
    {
        Assert.Equal(0.20f, ConditionOdds.ForRecipe(ConditionOdds.NormalRecipeFlags, jobLevel: 62)[(int)Condition.Good]);
    }

    [Theory]
    [InlineData((ushort)15)]
    [InlineData((ushort)115)]
    [InlineData((ushort)499)]
    [InlineData((ushort)1011)]
    [InlineData((ushort)1523)]
    [InlineData((ushort)547)] // Normal, Good, Sturdy, Good Omen — an unmeasured set, exercising the fallback
    public void Odds_AlwaysSumToOne(ushort flags)
    {
        Assert.Equal(1f, ConditionOdds.ForRecipe(flags, 100).Sum(), 3);
    }

    [Fact]
    public void UnmeasuredExpertSet_OnlyOffersTheConditionsItsFlagsName()
    {
        const ushort flags = 0b0010_0010_0011; // Normal, Good, Sturdy, GoodOmen
        var odds = ConditionOdds.ForRecipe(flags, 100);

        Assert.True(odds[(int)Condition.Sturdy] > 0);
        Assert.True(odds[(int)Condition.GoodOmen] > 0);
        Assert.Equal(0f, odds[(int)Condition.Pliant]);
        Assert.Equal(0f, odds[(int)Condition.Excellent]);
    }

    [Fact]
    public void IsExpert_IsAnythingBeyondTheBasicFour()
    {
        Assert.False(ConditionOdds.IsExpert(ConditionOdds.NormalRecipeFlags));
        Assert.True(ConditionOdds.IsExpert(ExpertFlags));
    }

    [Theory]
    [InlineData(Condition.Excellent, Condition.Poor)]
    [InlineData(Condition.Poor, Condition.Normal)]
    [InlineData(Condition.GoodOmen, Condition.Good)]
    [InlineData(Condition.Robust, Condition.Sturdy)]
    public void ForcedTransitions_IgnoreTheRoll(Condition current, Condition expected)
    {
        var odds = ConditionOdds.ForRecipe(ExpertFlags, 100);

        Assert.Equal(expected, ConditionOdds.Next(odds, expert: true, current, roll: 0.0f));
        Assert.Equal(expected, ConditionOdds.Next(odds, expert: true, current, roll: 0.999f));
    }

    [Fact]
    public void Good_ForcesNormal_OnlyOnNonExpertRecipes()
    {
        var normal = ConditionOdds.ForRecipe(ConditionOdds.NormalRecipeFlags, 100);
        Assert.Equal(Condition.Normal, ConditionOdds.Next(normal, expert: false, Condition.Good, roll: 0.0f));

        var expert = ConditionOdds.ForRecipe(ExpertFlags, 100);
        Assert.Equal(Condition.Good, ConditionOdds.Next(expert, expert: true, Condition.Good, roll: 0.0f));
    }

    [Fact]
    public void Roll_WalksTheOddsInConditionOrder()
    {
        var odds = ConditionOdds.ForRecipe(ConditionOdds.NormalRecipeFlags, 100); // Good .25, Excellent .04

        Assert.Equal(Condition.Good, ConditionOdds.Next(odds, false, Condition.Normal, 0.10f));
        Assert.Equal(Condition.Excellent, ConditionOdds.Next(odds, false, Condition.Normal, 0.27f));
        Assert.Equal(Condition.Normal, ConditionOdds.Next(odds, false, Condition.Normal, 0.50f));
    }

    [Fact]
    public void RolledFrequencies_MatchTheOdds()
    {
        var odds = ConditionOdds.ForRecipe(ConditionOdds.NormalRecipeFlags, 100);
        var random = new Random(7);
        var good = 0;
        const int rolls = 100_000;

        for (var i = 0; i < rolls; i++)
        {
            if (ConditionOdds.Next(odds, false, Condition.Normal, random.NextSingle()) == Condition.Good)
                good++;
        }

        Assert.InRange((double)good / rolls, 0.24, 0.26);
    }
}
