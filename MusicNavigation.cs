namespace EventDeck;

/// <summary>Ordering/auto-next policy only; every launch goes through MusicSession.</summary>
public sealed class MusicNavigation : IDisposable
{
    private readonly MusicSession _session;
    private readonly Func<IReadOnlyList<MusicLibraryEntry>> _visibleEntries;
    private readonly Dictionary<AudioTrack, MusicCollection?> _contexts = new();
    private string? _autoPendingSource;
    private bool _autoPlay;
    public event EventHandler<MusicLibraryEntry>? Navigated;

    public MusicNavigation(MusicSession session, Func<IReadOnlyList<MusicLibraryEntry>> visibleEntries)
    {
        _session = session;
        _visibleEntries = visibleEntries;
        _session.NaturallyEnded += OnNaturallyEnded;
        _session.StateChanged += PruneContexts;
        _session.PreparedChanged += PruneContexts;
    }

    public bool AutoPlay
    {
        get => _autoPlay;
        set
        {
            _autoPlay = value;
            if (!value && _autoPendingSource is { } source && _session.IsPlayPending
                && string.Equals(_session.PreparedTrack?.Source, source, StringComparison.OrdinalIgnoreCase))
                _session.CancelPendingPlay();
            if (!value) _autoPendingSource = null;
        }
    }

    public void Prepare(MusicLibraryEntry entry, double volume, bool previewMuted, MusicCollection? collection = null)
    {
        _autoPendingSource = null;
        _session.Prepare(entry.Title, entry.Source, volume, previewMuted);
        if (_session.PreparedTrack is { } track) _contexts[track] = collection;
    }

    public void LaunchPrepared()
    {
        _autoPendingSource = null;
        _session.PlayPrepared();
    }

    private AudioTrack? ReferenceTrack => _session.IsPlayPending ? _session.PreparedTrack
        : _session.PlayingTracks.LastOrDefault() ?? _session.PreparedTrack;

    public string? ContextName => ReferenceTrack is { } track && _contexts.TryGetValue(track, out var collection)
        && collection is not null ? collection.IsDeleted ? "Colección eliminada · avance deshabilitado" : collection.Name : null;

    // Collection context is bound to the track, never to the visible tab/search.
    private IReadOnlyList<MusicLibraryEntry> Sequence(AudioTrack track)
    {
        if (_contexts.TryGetValue(track, out var collection) && collection is not null)
            return collection.IsDeleted ? Array.Empty<MusicLibraryEntry>() : collection.Library.Entries;
        return _visibleEntries();
    }

    public MusicLibraryEntry? Neighbor(int direction) => ReferenceTrack is { } track
        ? FindNeighbor(Sequence(track), track.Source, direction) : null;

    public static MusicLibraryEntry? FindNeighbor(IReadOnlyList<MusicLibraryEntry> entries, string? source, int direction)
    {
        if (source is null || direction is not (-1 or 1)) return null;
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Source.Equals(source, StringComparison.OrdinalIgnoreCase)) continue;
            var index = i + direction;
            return index >= 0 && index < entries.Count ? entries[index] : null;
        }
        return null; // A filtered-out/unrelated resource is never used as a guessed anchor.
    }

    public void Move(int direction, double volume)
    {
        if (ReferenceTrack is { } track && Neighbor(direction) is { } entry)
            PrepareAndLaunch(entry, volume, false, _contexts.GetValueOrDefault(track));
    }

    private void OnNaturallyEnded(object? sender, AudioTrack track)
    {
        if (!AutoPlay || _session.IsPlayPending || !ReferenceEquals(_session.PlayingTracks.LastOrDefault(), track)) return;
        if (FindNeighbor(Sequence(track), track.Source, 1) is { } entry)
            PrepareAndLaunch(entry, track.Volume, true, _contexts.GetValueOrDefault(track));
    }

    private void PrepareAndLaunch(MusicLibraryEntry entry, double volume, bool automatic, MusicCollection? collection)
    {
        Prepare(entry, volume, true, collection);
        Navigated?.Invoke(this, entry);
        // Failed/missing media stays an error: don't skip arbitrarily through the list.
        if (_session.PreparedTrack is null) return;
        LaunchPrepared();
        _autoPendingSource = automatic ? entry.Source : null;
    }

    private void PruneContexts(object? sender, EventArgs e)
    {
        foreach (var track in _contexts.Keys.ToList())
            if (!ReferenceEquals(_session.PreparedTrack, track) && !_session.PlayingTracks.Contains(track)) _contexts.Remove(track);
    }

    public void Reset()
    {
        AutoPlay = false;
        _autoPendingSource = null;
        _contexts.Clear();
    }

    public void Dispose()
    {
        _session.NaturallyEnded -= OnNaturallyEnded;
        _session.StateChanged -= PruneContexts;
        _session.PreparedChanged -= PruneContexts;
        _contexts.Clear();
    }
}
