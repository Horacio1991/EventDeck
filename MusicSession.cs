using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace EventDeck;

public enum MusicPreparationState { Empty, Loading, Ready, PreviewPlaying, PreviewPaused, Error }
public sealed record MusicPreparationError(string Name, string Source, Exception Error, bool MissingFile);

/// <summary>
/// Owns music players, preparation and fades on the creating STA dispatcher.
/// Independent of MainWindow, video PREVIEW/PROGRAM and any particular music view.
/// </summary>
public sealed class MusicSession : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly ObservableCollection<AudioTrack> _playingTracks = new();
    private readonly Dictionary<AudioTrack, Action> _detachHandlers = new();
    private readonly Dictionary<AudioTrack, Task> _fadeOutTasks = new();
    private readonly HashSet<AudioTrack> _manualFadeOutRequested = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DispatcherTimer? _openTimer;
    private AudioTrack? _openTimerTrack;
    private bool _previewActive;
    private bool _playRequested;
    private bool _disposed;
    private int _generation;

    public ReadOnlyObservableCollection<AudioTrack> PlayingTracks { get; }
    public AudioTrack? PreparedTrack { get; private set; }
    public MusicPreparationState PreparationState { get; private set; }
    public MusicPreparationError? PreparationError { get; private set; }
    public bool PreparedHasDuration => PreparedTrack?.Player.NaturalDuration.HasTimeSpan == true;
    public TimeSpan PreparedPosition => PreparedTrack?.Player.Position ?? TimeSpan.Zero;
    public int PlayingCount => _playingTracks.Count(t => t.StateText == "REPRODUCIENDO");
    public event EventHandler? PreparedChanged;
    public event EventHandler? StateChanged;
    public event EventHandler<string>? StatusChanged;

    public MusicSession()
    {
        PlayingTracks = new(_playingTracks);
        _clock.Tick += Clock_Tick;
        _clock.Start();
    }

    private void VerifyAccess()
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
    private bool Owns(AudioTrack track) => !_disposed && _detachHandlers.ContainsKey(track);
    private void Status(string message) => StatusChanged?.Invoke(this, message);
    private void SetPreparedState(MusicPreparationState state)
    {
        PreparationState = state;
        PreparedChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Prepare(string name, string source, double volume, bool previewMuted)
    {
        VerifyAccess();
        StopOpenTimeout();
        if (PreparedTrack is { } previous) Release(previous);
        PreparedTrack = null; _previewActive = false; _playRequested = false;
        PreparationError = null;
        if (!File.Exists(source))
        {
            PreparationError = new(name, source, new FileNotFoundException("No se encuentra el archivo de audio.", source), true);
            SetPreparedState(MusicPreparationState.Error);
            Status($"EventDeck pudo preparar la referencia a ‘{Path.GetFileName(source)}’, pero el archivo no existe y no puede reproducirse.");
            return;
        }
        AudioTrack track;
        try { track = CreateTrack(name, source, volume); }
        catch (Exception ex) { PreparedFailed(name, source, ex); return; }
        PreparedTrack = track;
        track.PreviewMuted = previewMuted;
        SetPreparedState(MusicPreparationState.Loading);
        Status($"{name} preparada. La música que está sonando continúa.");
        StartOpenTimeout(track);
        try { track.Player.Open(new Uri(source)); }
        catch (Exception ex) { MediaFailed(track, ex); }
    }

    private AudioTrack CreateTrack(string name, string source, double volume)
    {
        var player = new MediaPlayer { Volume = volume };
        var track = new AudioTrack(name, source, player) { Volume = volume, StateText = "PREPARANDO" };
        EventHandler opened = (_, _) => _dispatcher.BeginInvoke(new Action(() => MediaOpened(track)));
        EventHandler ended = (_, _) => _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!Owns(track)) return;
            track.CurrentSeconds = track.DurationSeconds; track.StateText = "FINALIZADA";
            if (ReferenceEquals(PreparedTrack, track))
            { _previewActive = false; SetPreparedState(MusicPreparationState.Ready); }
            Changed();
        }));
        EventHandler<ExceptionEventArgs> failed = (_, args) =>
        {
            var error = args.ErrorException ?? new InvalidOperationException("Windows no informó el detalle del error multimedia.");
            _dispatcher.BeginInvoke(new Action(() => MediaFailed(track, error)));
        };
        player.MediaOpened += opened;
        player.MediaEnded += ended;
        player.MediaFailed += failed;
        _detachHandlers.Add(track, () =>
        {
            player.MediaOpened -= opened;
            player.MediaEnded -= ended;
            player.MediaFailed -= failed;
        });
        return track;
    }

    private void MediaOpened(AudioTrack track)
    {
        if (!Owns(track) || !ReferenceEquals(PreparedTrack, track)) return;
        if (!track.Player.HasAudio)
        {
            MediaFailed(track, new InvalidDataException("Windows abrió el archivo, pero no encontró una pista de audio reproducible."));
            return;
        }
        track.IsPrepared = true; track.StateText = "LISTA";
        StopOpenTimeout(track);
        if (track.Player.NaturalDuration.HasTimeSpan)
            track.DurationSeconds = track.Player.NaturalDuration.TimeSpan.TotalSeconds;
        SetPreparedState(MusicPreparationState.Ready);
        if (_playRequested) { _playRequested = false; PlayPrepared(); }
    }

    private void StartOpenTimeout(AudioTrack track)
    {
        StopOpenTimeout();
        _openTimerTrack = track;
        _openTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _openTimer.Tick += (_, _) =>
        {
            StopOpenTimeout(track);
            if (Owns(track) && ReferenceEquals(PreparedTrack, track) && !track.IsPrepared)
                MediaFailed(track, new TimeoutException("Windows no informó MediaOpened después de 20 segundos."));
        };
        _openTimer.Start();
    }
    private void StopOpenTimeout(AudioTrack? track = null)
    {
        if (track is not null && !ReferenceEquals(_openTimerTrack, track)) return;
        _openTimer?.Stop();
        _openTimer = null; _openTimerTrack = null;
    }

    public void PlayPrepared()
    {
        VerifyAccess();
        if (PreparedTrack is not { } track) { Status("Elegí una canción en la biblioteca para prepararla."); return; }
        if (!File.Exists(track.Source))
        { MediaFailed(track, new FileNotFoundException("No se encuentra el archivo de audio.", track.Source)); return; }
        if (!track.IsPrepared)
        {
            _playRequested = true;
            Status($"Reproducción de {track.Name} solicitada; comenzará cuando Windows termine de abrir el medio.");
            return;
        }
        var oldTracks = _playingTracks.Where(t => t.StateText == "REPRODUCIENDO").ToList();
        try
        {
            track.Player.Pause();
            if (track.Player.NaturalDuration.HasTimeSpan) track.Player.Position = TimeSpan.Zero;
            track.PlaybackGain = oldTracks.Count > 0 ? 0 : 1;
            track.PreviewMuted = false;
            track.Player.Play();
        }
        catch (Exception ex) { MediaFailed(track, ex); return; }
        track.StateText = "REPRODUCIENDO";
        PreparedTrack = null; _playRequested = false; _previewActive = false;
        _playingTracks.Add(track);
        foreach (var old in oldTracks) _ = FadeOutTrackAsync(old, 1200);
        if (oldTracks.Count > 0) _ = FadeInAsync(track);
        SetPreparedState(MusicPreparationState.Empty);
        Changed();
        Status(oldTracks.Count == 0 ? $"Reproduciendo {track.Name}." : $"Reproduciendo {track.Name}; fundiendo las pistas anteriores.");
    }

    private void MediaFailed(AudioTrack track, Exception error)
    {
        if (!Owns(track)) return;
        var wasPrepared = ReferenceEquals(PreparedTrack, track);
        StopOpenTimeout(track);
        track.IsPrepared = false; track.StateText = "ERROR";
        if (wasPrepared)
        {
            PreparedTrack = null; _previewActive = false; _playRequested = false;
            Release(track);
            PreparedFailed(track.Name, track.Source, error);
        }
        else
        {
            RemoveTrack(track);
            Status($"EventDeck pudo preparar la referencia a ‘{Path.GetFileName(track.Source)}’, pero no pudo reproducir el medio. Error informado por Windows: {error.Message}");
        }
    }
    private void PreparedFailed(string name, string source, Exception error)
    {
        PreparationError = new(name, source, error, false);
        SetPreparedState(MusicPreparationState.Error);
        Status($"EventDeck pudo preparar la referencia a ‘{Path.GetFileName(source)}’, pero no pudo reproducir el medio. Error informado por Windows: {error.Message}");
    }

    public void PlayPreview(bool muted)
    {
        VerifyAccess();
        if (PreparedTrack is not { } track || !track.Player.NaturalDuration.HasTimeSpan) return;
        if (track.Player.Position >= track.Player.NaturalDuration.TimeSpan) track.Player.Position = TimeSpan.Zero;
        track.PreviewMuted = muted; track.Player.Play(); _previewActive = true;
        SetPreparedState(MusicPreparationState.PreviewPlaying);
    }
    public void PausePreview()
    {
        VerifyAccess();
        if (PreparedTrack is not { } track) return;
        track.Player.Pause(); _previewActive = false;
        SetPreparedState(MusicPreparationState.PreviewPaused);
    }
    public void StopPreview()
    {
        VerifyAccess();
        if (PreparedTrack is not { } track) return;
        track.Player.Pause();
        if (track.Player.NaturalDuration.HasTimeSpan) track.Player.Position = TimeSpan.Zero;
        _previewActive = false;
        SetPreparedState(MusicPreparationState.Ready);
    }
    public void StopActivePreview(bool muted)
    {
        VerifyAccess();
        if (PreparedTrack is not { } track || !_previewActive) return;
        track.Player.Pause(); track.Player.Position = TimeSpan.Zero;
        track.PreviewMuted = muted; _previewActive = false;
        SetPreparedState(MusicPreparationState.Ready);
    }
    public void SetPreviewMuted(bool muted)
    {
        VerifyAccess();
        if (PreparedTrack is { } track) track.PreviewMuted = muted;
    }
    public void SeekPrepared(TimeSpan position)
    {
        VerifyAccess();
        if (PreparedTrack is { } track) track.Player.Position = position;
    }
    public void SetPreparedVolume(double volume)
    {
        VerifyAccess();
        if (PreparedTrack is { } track) track.Volume = volume;
    }
    public void ToggleTrack(AudioTrack track)
    {
        VerifyAccess();
        if (!_playingTracks.Contains(track)) return;
        if (track.StateText == "REPRODUCIENDO") { track.Player.Pause(); track.StateText = "EN PAUSA"; }
        else
        {
            if (track.StateText == "FINALIZADA" || track.StateText == "DETENIDA") track.Player.Position = TimeSpan.Zero;
            track.PlaybackGain = 1; track.Player.Play(); track.StateText = "REPRODUCIENDO";
        }
        Changed();
    }
    public void SeekTrack(AudioTrack track, double seconds)
    {
        VerifyAccess();
        if (_playingTracks.Contains(track)) track.Player.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
    }
    public void SetVolume(AudioTrack track, double volume)
    {
        VerifyAccess();
        if (Owns(track)) track.Volume = volume;
    }
    public void RemovePrepared()
    {
        VerifyAccess();
        if (PreparedTrack is not { } track) return;
        PreparedTrack = null; _previewActive = false; _playRequested = false; PreparationError = null;
        Release(track);
        SetPreparedState(MusicPreparationState.Empty);
    }
    public void RemoveTrack(AudioTrack track)
    {
        VerifyAccess();
        if (!_playingTracks.Remove(track)) return;
        Release(track);
        Changed();
    }
    public Task RemoveWithFadeAsync(AudioTrack track)
    {
        VerifyAccess();
        return FadeOutTrackAsync(track, 350);
    }
    public async Task ClearAsync()
    {
        VerifyAccess();
        var generation = _generation;
        var playing = _playingTracks.Where(t => t.StateText == "REPRODUCIENDO").ToList();
        foreach (var track in playing) track.StateText = "QUITANDO";
        if (playing.Count > 0) await Task.WhenAll(playing.Select(t => FadeOutTrackAsync(t, 450)));
        if (_disposed || generation != _generation) return;
        foreach (var track in _playingTracks.ToList()) RemoveTrack(track);
        if (PreparedTrack is not null)
        {
            RemovePrepared();
            Status("Pista preparada quitada. El archivo original se conserva.");
        }
        Changed();
    }

    public async Task FadeOutCurrentAsync()
    {
        VerifyAccess();
        var tracks = _playingTracks.Where(t => t.StateText == "REPRODUCIENDO").ToList();
        if (tracks.Count == 0) return;
        foreach (var track in tracks) _manualFadeOutRequested.Add(track);
        Status($"Fade-out de {tracks.Count} pista(s) de audio.");
        await Task.WhenAll(tracks.Select(track => FadeOutTrackAsync(track, 1000)));
    }
    private Task FadeOutTrackAsync(AudioTrack track, int durationMs)
    {
        if (!_playingTracks.Contains(track)) return Task.CompletedTask;
        if (_fadeOutTasks.TryGetValue(track, out var activeFade)) return activeFade;
        var task = FadeOutTrackCoreAsync(track, durationMs);
        _fadeOutTasks[track] = task;
        return task;
    }
    private async Task FadeOutTrackCoreAsync(AudioTrack track, int durationMs)
    {
        try
        {
            var start = track.PlaybackGain;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < durationMs && _playingTracks.Contains(track))
            {
                await Task.Delay(25);
                // Reset/Dispose may have released the player while the delay was pending.
                if (!Owns(track)) return;
                var progress = Math.Clamp(timer.Elapsed.TotalMilliseconds / durationMs, 0, 1);
                track.PlaybackGain = start * (1 - progress);
            }
            if (_playingTracks.Contains(track)) { track.PlaybackGain = 0; RemoveTrack(track); }
        }
        catch (Exception ex)
        {
            if (Owns(track))
            {
                RemoveTrack(track);
                Status($"No se pudo completar el fade-out de {track.Name}: {ex.Message}");
            }
        }
        finally { _fadeOutTasks.Remove(track); _manualFadeOutRequested.Remove(track); }
    }
    private async Task FadeInAsync(AudioTrack track)
    {
        const int durationMs = 650; var elapsed = 0;
        while (elapsed < durationMs && _playingTracks.Contains(track) && track.StateText == "REPRODUCIENDO" && !_manualFadeOutRequested.Contains(track))
        {
            await Task.Delay(40); elapsed += 40;
            if (!Owns(track)) return;
            if (!_manualFadeOutRequested.Contains(track)) track.PlaybackGain = Math.Min(1, elapsed / (double)durationMs);
        }
        if (_playingTracks.Contains(track) && track.StateText == "REPRODUCIENDO" && !_manualFadeOutRequested.Contains(track)) track.PlaybackGain = 1;
    }

    private void Clock_Tick(object? sender, EventArgs e)
    {
        foreach (var track in _playingTracks)
        {
            if (!track.Player.NaturalDuration.HasTimeSpan) continue;
            track.CurrentSeconds = Math.Min(track.Player.Position.TotalSeconds, track.DurationSeconds);
        }
    }
    private void Release(AudioTrack track)
    {
        StopOpenTimeout(track);
        if (!_detachHandlers.Remove(track, out var detach)) return;
        detach();
        track.IsPrepared = false;
        track.Player.Close();
    }
    public void Reset()
    {
        VerifyAccess();
        ResetCore();
        SetPreparedState(MusicPreparationState.Empty);
        Changed();
    }
    private void ResetCore()
    {
        _generation++;
        StopOpenTimeout();
        PreparedTrack = null; _previewActive = false; _playRequested = false; PreparationError = null;
        _playingTracks.Clear();
        foreach (var track in _detachHandlers.Keys.ToList()) Release(track);
        _manualFadeOutRequested.Clear();
        _fadeOutTasks.Clear();
    }
    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _clock.Stop(); _clock.Tick -= Clock_Tick;
        ResetCore();
        PreparationState = MusicPreparationState.Empty;
    }
}
