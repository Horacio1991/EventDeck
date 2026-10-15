using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace EventDeck;

public sealed class AudioTrack : INotifyPropertyChanged
{
    private double _currentSeconds;
    private double _durationSeconds;
    private double _volume = 0.8;
    private double _playbackGain = 1;
    private bool _previewMuted;
    private string _stateText = "DETENIDA";

    public string Name { get; }
    public string Source { get; }
    public MediaPlayer Player { get; }
    public bool IsPrepared { get; set; }
    public double CurrentSeconds
    {
        get => _currentSeconds;
        set { if (Set(ref _currentSeconds, value)) OnPropertyChanged(nameof(TimeLabel)); }
    }
    public double DurationSeconds
    {
        get => _durationSeconds;
        set { if (Set(ref _durationSeconds, value)) OnPropertyChanged(nameof(TimeLabel)); }
    }
    public double Volume
    {
        get => _volume;
        set { if (Set(ref _volume, Math.Clamp(value, 0, 1))) ApplyEffectiveVolume(); }
    }
    public double PlaybackGain
    {
        get => _playbackGain;
        set { _playbackGain = Math.Clamp(value, 0, 1); ApplyEffectiveVolume(); }
    }
    public bool PreviewMuted
    {
        get => _previewMuted;
        set { _previewMuted = value; ApplyEffectiveVolume(); }
    }
    public string StateText { get => _stateText; set => Set(ref _stateText, value); }
    public string TimeLabel => $"{Format(CurrentSeconds)} / {Format(DurationSeconds)}";

    public AudioTrack(string name, string source, MediaPlayer player) { Name = name; Source = source; Player = player; }

    private void ApplyEffectiveVolume()
    {
        var effective = _volume * _playbackGain;
        Player.Volume = effective;
        Player.IsMuted = effective <= 0 || _previewMuted;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var value = TimeSpan.FromSeconds(seconds);
        return $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    }
}
