using System.ComponentModel;
using System.IO;

namespace EventDeck;

/// <summary>Local music reference, independent of audiovisual bins and playback.</summary>
public sealed class MusicLibraryEntry : INotifyPropertyChanged
{
    public string Source { get; }
    public string Title { get; }
    public TimeSpan? Duration { get; private set; }
    public string DurationStatus { get; private set; } = "Leyendo duración…";
    public string DurationLabel => Duration is { } duration
        ? $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}"
        : DurationStatus == "Leyendo duración…" ? "…" : "--:--";

    public MusicLibraryEntry(string source)
    {
        Source = Path.GetFullPath(source);
        Title = Path.GetFileNameWithoutExtension(source);
    }

    public bool Matches(string? query)
    {
        var term = query?.Trim() ?? "";
        return Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(Source).Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    internal void SetDuration(TimeSpan? duration, string status)
    {
        Duration = duration;
        DurationStatus = status;
        PropertyChanged?.Invoke(this, new(nameof(Duration)));
        PropertyChanged?.Invoke(this, new(nameof(DurationLabel)));
        PropertyChanged?.Invoke(this, new(nameof(DurationStatus)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
