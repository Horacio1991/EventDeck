namespace EventDeck;

/// <summary>Ordering/auto-next policy only; every launch goes through MusicSession.</summary>
public sealed class MusicNavigation : IDisposable
{
    private readonly MusicSession _session;
    private readonly Func<IReadOnlyList<MusicLibraryEntry>> _visibleEntries;
    private string? _autoPendingSource;
    private bool _autoPlay;
    public event EventHandler<MusicLibraryEntry>? Navigated;

    public MusicNavigation(MusicSession session, Func<IReadOnlyList<MusicLibraryEntry>> visibleEntries)
    {
        _session = session;
        _visibleEntries = visibleEntries;
        _session.NaturallyEnded += OnNaturallyEnded;
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

    public void Prepare(MusicLibraryEntry entry, double volume, bool previewMuted)
    {
        _autoPendingSource = null;
        _session.Prepare(entry.Title, entry.Source, volume, previewMuted);
    }

    public void LaunchPrepared()
    {
        _autoPendingSource = null;
        _session.PlayPrepared();
    }

    public MusicLibraryEntry? Neighbor(int direction)
    {
        var source = _session.IsPlayPending ? _session.PreparedTrack?.Source
            : _session.PlayingTracks.LastOrDefault()?.Source ?? _session.PreparedTrack?.Source;
        return FindNeighbor(_visibleEntries(), source, direction);
    }

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
        if (Neighbor(direction) is { } entry) PrepareAndLaunch(entry, volume, false);
    }

    private void OnNaturallyEnded(object? sender, AudioTrack track)
    {
        if (!AutoPlay || _session.IsPlayPending || !ReferenceEquals(_session.PlayingTracks.LastOrDefault(), track)) return;
        if (FindNeighbor(_visibleEntries(), track.Source, 1) is { } entry)
            PrepareAndLaunch(entry, track.Volume, true);
    }

    private void PrepareAndLaunch(MusicLibraryEntry entry, double volume, bool automatic)
    {
        Prepare(entry, volume, true);
        Navigated?.Invoke(this, entry);
        // Failed/missing media stays an error: don't skip arbitrarily through the list.
        if (_session.PreparedTrack is null) return;
        LaunchPrepared();
        _autoPendingSource = automatic ? entry.Source : null;
    }

    public void Dispose() => _session.NaturallyEnded -= OnNaturallyEnded;
}
