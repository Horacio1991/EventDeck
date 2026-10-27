using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace EventDeck;

/// <summary>
/// Temporary local library for the main window's lifetime. Does not own playback.
/// Duration probes open one silent MediaPlayer at a time, never call Play, and close it.
/// All calls and MediaPlayer events stay on the creating WPF dispatcher.
/// </summary>
public sealed class MusicLibrary : IDisposable
{
    private readonly ObservableCollection<MusicLibraryEntry> _entries = new();
    private readonly Queue<MusicLibraryEntry> _pending = new();
    private readonly CancellationTokenSource _lifetime = new();
    private MediaPlayer? _probe;
    private bool _reading;
    private bool _disposed;
    public ReadOnlyObservableCollection<MusicLibraryEntry> Entries { get; }

    public MusicLibrary() => Entries = new(_entries);

    public void Add(IEnumerable<string> sources)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var source in sources)
        {
            var path = Path.GetFullPath(source);
            if (_entries.Any(e => e.Source.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new MusicLibraryEntry(path);
            _entries.Add(entry);
            _pending.Enqueue(entry);
        }
    }

    public async Task ReadPendingDurationsAsync()
    {
        if (_disposed || _reading) return;
        _reading = true;
        try
        {
            while (!_disposed && _pending.TryDequeue(out var entry))
                await ReadDurationAsync(entry);
        }
        finally { _reading = false; }
    }

    private async Task ReadDurationAsync(MusicLibraryEntry entry)
    {
        if (!File.Exists(entry.Source))
        {
            entry.SetDuration(null, "Archivo no encontrado");
            return;
        }
        var player = new MediaPlayer { Volume = 0, IsMuted = true };
        _probe = player;
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler opened = (_, _) => completed.TrySetResult(true);
        EventHandler<ExceptionEventArgs> failed = (_, _) => completed.TrySetResult(false);
        player.MediaOpened += opened;
        player.MediaFailed += failed;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try
        {
            player.Open(new Uri(entry.Source));
            var winner = await Task.WhenAny(completed.Task, Task.Delay(TimeSpan.FromSeconds(20), timeout.Token));
            if (_disposed) return;
            if (winner != completed.Task)
                entry.SetDuration(null, "Tiempo de lectura agotado; podés intentar preparar el archivo");
            else if (!await completed.Task || !player.HasAudio)
                entry.SetDuration(null, "Windows no pudo leer una pista de audio");
            else if (player.NaturalDuration.HasTimeSpan)
                entry.SetDuration(player.NaturalDuration.TimeSpan, "Duración del archivo");
            else
                entry.SetDuration(null, "Duración no disponible");
        }
        catch (Exception ex)
        {
            // Scoped to metadata reading; playback errors still use MusicSession's handling.
            if (!_disposed) entry.SetDuration(null, $"No se pudo leer la duración: {ex.Message}");
        }
        finally
        {
            timeout.Cancel();
            player.MediaOpened -= opened;
            player.MediaFailed -= failed;
            player.Close();
            if (ReferenceEquals(_probe, player)) _probe = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pending.Clear();
        _lifetime.Cancel();
        _probe?.Close();
        _probe = null;
        _lifetime.Dispose();
    }
}
