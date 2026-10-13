using Newtonsoft.Json;

namespace Hephaestus.Tests;

public class HephaestusConfigTests
{
    private const ulong Main = 0x0040_0000_1111_2222;
    private const ulong Alt = 0x0040_0000_3333_4444;

    [Fact]
    public void ModeFor_NoOverride_FollowsGlobalDefault()
    {
        var config = new HephaestusConfig { DefaultMode = ExecutionMode.Auto };

        Assert.Equal(ExecutionMode.Auto, config.ModeFor(Alt));
    }

    [Fact]
    public void ModeFor_Override_WinsForThatCharacterOnly()
    {
        var config = new HephaestusConfig { DefaultMode = ExecutionMode.Auto };
        config.CharacterFor(Main).ModeOverride = ExecutionMode.Assisted;

        Assert.Equal(ExecutionMode.Assisted, config.ModeFor(Main));
        Assert.Equal(ExecutionMode.Auto, config.ModeFor(Alt));
    }

    [Fact]
    public void ModeFor_OverrideCleared_FollowsGlobalAgain()
    {
        var config = new HephaestusConfig { DefaultMode = ExecutionMode.Auto };
        config.CharacterFor(Main).ModeOverride = ExecutionMode.Assisted;
        config.CharacterFor(Main).ModeOverride = null;

        config.DefaultMode = ExecutionMode.Assisted;
        Assert.Equal(ExecutionMode.Assisted, config.ModeFor(Main));
        config.DefaultMode = ExecutionMode.Auto;
        Assert.Equal(ExecutionMode.Auto, config.ModeFor(Main));
    }

    [Fact]
    public void CharacterFor_ReturnsTheSameEntryEachTime()
    {
        var config = new HephaestusConfig();

        Assert.Same(config.CharacterFor(Main), config.CharacterFor(Main));
        Assert.Single(config.Characters);
    }

    /// <summary>Dalamud persists the config with Newtonsoft — the content-id keys must survive it.</summary>
    [Fact]
    public void PerCharacterOverrides_SurviveDalamudSerialization()
    {
        var config = new HephaestusConfig { DefaultMode = ExecutionMode.Auto };
        config.CharacterFor(Main).ModeOverride = ExecutionMode.Assisted;

        var json = JsonConvert.SerializeObject(config);
        var loaded = JsonConvert.DeserializeObject<HephaestusConfig>(json)!;

        Assert.Equal(ExecutionMode.Assisted, loaded.ModeFor(Main));
        Assert.Equal(ExecutionMode.Auto, loaded.ModeFor(Alt));
    }

    [Fact]
    public void Migrate_CurrentVersion_ChangesNothing()
    {
        var config = new HephaestusConfig();

        Assert.False(config.Migrate());
        Assert.Equal(HephaestusConfig.CurrentVersion, config.Version);
    }

    [Fact]
    public void Migrate_OlderVersion_BumpsAndReportsChange()
    {
        var config = new HephaestusConfig { Version = 0 };

        Assert.True(config.Migrate());
        Assert.Equal(HephaestusConfig.CurrentVersion, config.Version);
    }
}
