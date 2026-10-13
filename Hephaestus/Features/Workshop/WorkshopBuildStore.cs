using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Hephaestus.Features.Workshop;

/// <summary>One part queued in the workshop build list.</summary>
public sealed class WorkshopBuildEntry
{
    public uint PartItemId { get; set; }
}

/// <summary>
/// Per-character workshop build list — which FC-workshop parts to build — persisted as
/// <c>workshop/{contentId}.json</c>, the same one-file-per-character shape as the queue and outcome stores.
/// </summary>
public sealed class WorkshopBuildStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public WorkshopBuildStore(string configDirectory) => _directory = Path.Combine(configDirectory, "workshop");

    public string PathFor(ulong contentId) => Path.Combine(_directory, $"{contentId:X16}.json");

    public List<WorkshopBuildEntry> Load(ulong contentId)
    {
        if (contentId == 0)
            return new();

        var path = PathFor(contentId);
        if (!File.Exists(path))
            return new();

        try
        {
            return JsonSerializer.Deserialize<List<WorkshopBuildEntry>>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new();
        }
    }

    public void Save(ulong contentId, List<WorkshopBuildEntry> entries)
    {
        if (contentId == 0)
            return;

        Directory.CreateDirectory(_directory);
        var path = PathFor(contentId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entries, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Add a part to the end of the build list if it is not already there, starting at phase 1.</summary>
    public void Add(ulong contentId, uint partItemId)
    {
        if (contentId == 0 || partItemId == 0)
            return;

        var entries = Load(contentId);
        if (entries.Any(e => e.PartItemId == partItemId))
            return;

        entries.Add(new WorkshopBuildEntry { PartItemId = partItemId });
        Save(contentId, entries);
    }

    public void Remove(ulong contentId, uint partItemId)
    {
        if (contentId == 0 || partItemId == 0)
            return;

        var entries = Load(contentId);
        if (entries.RemoveAll(e => e.PartItemId == partItemId) > 0)
            Save(contentId, entries);
    }
}
