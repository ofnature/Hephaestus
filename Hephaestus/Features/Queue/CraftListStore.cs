using System.IO;
using System.Text.Json;

namespace Hephaestus.Features.Queue;

/// <summary>
/// Persists one craft queue per character as <c>queues/{contentId}.json</c> under the plugin's
/// config directory.
///
/// Deliberately NOT in <see cref="HephaestusConfig"/>: that file is shared by every client
/// launched from the same XIVLauncher and the last save wins, so four boxes crafting at once would
/// overwrite each other's progress. A file per content id means each client only ever writes its
/// own.
/// </summary>
public sealed class CraftListStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public CraftListStore(string configDirectory)
    {
        _directory = Path.Combine(configDirectory, "queues");
    }

    public string PathFor(ulong contentId) => Path.Combine(_directory, $"{contentId:X16}.json");

    /// <summary>
    /// Load a character's queue; a missing file is an empty queue. An unreadable file is moved
    /// aside to <c>.corrupt</c> rather than overwritten, so a queue is never silently destroyed.
    /// </summary>
    public CraftList Load(ulong contentId)
    {
        var path = PathFor(contentId);
        if (!File.Exists(path))
            return new CraftList();

        try
        {
            return JsonSerializer.Deserialize<CraftList>(File.ReadAllText(path), JsonOptions) ?? new CraftList();
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new CraftList();
        }
    }

    /// <summary>Write-then-rename, so a crash mid-save cannot leave a half-written queue.</summary>
    public void Save(ulong contentId, CraftList list)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(contentId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
