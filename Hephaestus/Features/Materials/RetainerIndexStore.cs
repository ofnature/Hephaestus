using System.IO;
using System.Text.Json;

namespace Hephaestus.Features.Materials;

/// <summary>
/// Persists one <see cref="RetainerIndex"/> per character as <c>retainers/{contentId}.json</c>
/// under the plugin's config directory — the same reasoning as <see cref="Queue.CraftListStore"/>:
/// the plugin config is shared by every client of one XIVLauncher and the last save wins.
/// </summary>
public sealed class RetainerIndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;

    public RetainerIndexStore(string configDirectory)
    {
        _directory = Path.Combine(configDirectory, "retainers");
    }

    public string PathFor(ulong contentId) => Path.Combine(_directory, $"{contentId:X16}.json");

    /// <summary>A missing file is an empty index; an unreadable one is moved aside to <c>.corrupt</c>, never overwritten.</summary>
    public RetainerIndex Load(ulong contentId)
    {
        var path = PathFor(contentId);
        if (!File.Exists(path))
            return new RetainerIndex();

        try
        {
            return JsonSerializer.Deserialize<RetainerIndex>(File.ReadAllText(path), JsonOptions) ?? new RetainerIndex();
        }
        catch (JsonException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return new RetainerIndex();
        }
    }

    /// <summary>Write-then-rename, so a crash mid-save cannot leave a half-written index.</summary>
    public void Save(ulong contentId, RetainerIndex index)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(contentId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>The logged-in character's retainer index, following the character like <see cref="Queue.CharacterQueue"/>.</summary>
public sealed class CharacterRetainers
{
    private readonly RetainerIndexStore _store;

    public CharacterRetainers(RetainerIndexStore store)
    {
        _store = store;
    }

    /// <summary>0 while nobody is logged in.</summary>
    public ulong ContentId { get; private set; }

    public RetainerIndex Index { get; private set; } = new();

    /// <returns>True when the character changed and a different index was loaded.</returns>
    public bool Follow(ulong contentId)
    {
        if (contentId == ContentId)
            return false;

        ContentId = contentId;
        Index = contentId == 0 ? new RetainerIndex() : _store.Load(contentId);
        return true;
    }

    public void Save()
    {
        if (ContentId != 0)
            _store.Save(ContentId, Index);
    }

    public string FilePath => _store.PathFor(ContentId);
}
