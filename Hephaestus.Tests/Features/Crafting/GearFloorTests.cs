using Hephaestus.Features.Crafting;

namespace Hephaestus.Tests.Features.Crafting;

/// <summary>Hephaestus.FinishUnderGearGate (2026-11-15): the work in hand may go on under the player's gear gate, down to a floor.</summary>
public class GearFloorTests
{
    private const int Gate = 20;

    [Fact]
    public void WithNoFloorAsked_ThePlayersGateHolds()
    {
        var floor = new GearFloor();

        Assert.Equal(0, floor.Percent);
        Assert.Equal(Gate, floor.Gate(Gate));
    }

    [Fact]
    public void AFloorForTheWorkInHand_LowersTheGate_UntilItIsDropped()
    {
        var floor = new GearFloor();

        Assert.True(floor.Request(askedPercent: 10, Gate, gearPercent: 19, workInHand: true));
        Assert.Equal(10, floor.Percent);
        Assert.Equal(10, floor.Gate(Gate));

        floor.Drop();
        Assert.Equal(0, floor.Percent);
        Assert.Equal(Gate, floor.Gate(Gate));
    }

    [Theory]
    [InlineData(0, 5)]   // nobody goes under the hard floor...
    [InlineData(-3, 5)]
    [InlineData(1, 5)]
    [InlineData(12, 12)]
    [InlineData(20, 20)] // ...and nobody RAISES the player's gate
    [InlineData(50, 20)]
    public void WhatARequestComesTo_IsNeverUnderTheHardFloor_NorAboveThePlayersGate(int asked, int expected)
    {
        var floor = new GearFloor();

        Assert.True(floor.Request(asked, Gate, gearPercent: 100, workInHand: true));
        Assert.Equal(expected, floor.Percent);
        Assert.Equal(expected, floor.Gate(Gate));
    }

    [Fact]
    public void APlayersGateUnderTheHardFloor_IsLeftAsItIs()
    {
        var floor = new GearFloor();

        Assert.True(floor.Request(askedPercent: 1, playerGatePercent: 3, gearPercent: 4, workInHand: true));
        Assert.Equal(3, floor.Gate(3));
    }

    [Fact]
    public void WithNothingInHand_ItIsRefused_AndNothingChanges()
    {
        var floor = new GearFloor();

        Assert.False(floor.Request(10, Gate, gearPercent: 19, workInHand: false));
        Assert.Equal(0, floor.Percent);
    }

    [Fact]
    public void GearAlreadyUnderTheFloor_IsRefused_AndAFloorTakenBeforeStays()
    {
        var floor = new GearFloor();
        Assert.True(floor.Request(10, Gate, gearPercent: 15, workInHand: true));

        Assert.False(floor.Request(12, Gate, gearPercent: 11, workInHand: true));
        Assert.Equal(10, floor.Percent);

        Assert.False(new GearFloor().Request(0, Gate, gearPercent: 4, workInHand: true)); // under the hard floor: never
    }

    [Fact]
    public void ThePlayersGateMovedUnderTheFloor_Wins()
    {
        // The player moves the slider under the floor while it holds: their gate wins.
        var floor = new GearFloor();
        Assert.True(floor.Request(10, Gate, gearPercent: 19, workInHand: true));

        Assert.Equal(8, floor.Gate(8));
    }
}
