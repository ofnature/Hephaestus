using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Hephaestus.Features.Crafting;

/// <summary>One recipe's tracked outcome across this character's crafts.</summary>
public sealed class CraftOutcomeEntry
{
    public int Crafts { get; set; }
    public int Hq { get; set; }
}

/// <summary>
/// Per-character craft outcomes — how many of each recipe have been made, and how many of those came
/// out HQ — persisted as <c>outcomes/{contentId}.json</c> under the config directory, the same
/// one-file-per-character shape as <see cref="Queue.CraftListStore"/>. The crafting window shows the
/// running HQ rate from this, not a stat prediction: the real rate the player has actually seen.
/// </summary>
public sealed class CraftOutcomeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public CraftOutcomeStore(string configDirectory)
    {
        _directory = Path.Combine(configDirectory, "outcomes");
    }

    public string PathFor(ulong contentId) => Path.Combine(_directory, $"{contentId:X16}.json");

    /// <summary>Add one completed craft to the recipe's tally.</summary>
    public void Record(ulong contentId, uint recipeId, bool hq)
    {
        if (contentId == 0 || recipeId == 0)
            return;

        var entries = Load(contentId);
        if (!entries.TryGetValue(recipeId, out var entry))
            entries[recipeId] = entry = new CraftOutcomeEntry();

        entry.Crafts++;
        if (hq)
            entry.Hq++;
        Save(contentId, entries);
    }

    /// <summary>The recipe's tally, or null when it has never been crafted by this character.</summary>
    public (int Crafts, int Hq)? Lookup(ulong contentId, uint recipeId)
    {
        if (contentId == 0 || recipeId == 0)
            return null;

        return Load(contentId).TryGetValue(recipeId, out var entry) ? (entry.Crafts, entry.Hq) : null;
    }

    private Dictionary<uint, CraftOutcomeEntry> Load(ulong contentId)
    {
        var path = PathFor(contentId);
        if (!File.Exists(path))
            return new();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<uint, CraftOutcomeEntry>>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new();
        }
    }

    private void Save(ulong contentId, Dictionary<uint, CraftOutcomeEntry> entries)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(contentId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
