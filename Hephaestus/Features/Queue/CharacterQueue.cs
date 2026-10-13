namespace Hephaestus.Features.Queue;

/// <summary>
/// The logged-in character's queue, loaded once and shared by everything that touches it — the
/// window that edits it and the engine that works through it — so they are always looking at the
/// same list object. Follows the character: a different content id loads a different file.
/// </summary>
public sealed class CharacterQueue
{
    private readonly CraftListStore _store;

    public CharacterQueue(CraftListStore store)
    {
        _store = store;
    }

    /// <summary>0 while nobody is logged in.</summary>
    public ulong ContentId { get; private set; }

    public CraftList List { get; private set; } = new();

    /// <returns>True when the character changed and a different list was loaded.</returns>
    public bool Follow(ulong contentId)
    {
        if (contentId == ContentId)
            return false;

        ContentId = contentId;
        List = contentId == 0 ? new CraftList() : _store.Load(contentId);
        return true;
    }

    public void Save()
    {
        if (ContentId != 0)
            _store.Save(ContentId, List);
    }

    public string FilePath => _store.PathFor(ContentId);
}
