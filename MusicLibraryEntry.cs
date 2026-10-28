using System.ComponentModel;
using System.IO;

namespace EventDeck;

/// <summary>Local music reference, independent of audiovisual bins and playback.</summary>
public sealed class MusicLibraryEntry : INotifyPropertyChanged
{
    public string Source { get; }
    public string Title { get; }
    public bool IsMissing => !File.Exists(Source);
    public TimeSpan? Duration { get; private set; }
    public string DurationStatus { get; private set; } = "Leyendo duración…";
    public string DurationLabel => Duration is { } duration
        ? $"{(int)duration.TotalMinutes:00}:{duration.Seconds:00}"
        : DurationStatus == "Leyendo duración…" ? "…" : "--:--";

    public MusicLibraryEntry(string source, string? title = null)
    {
        // Preserve invalid/missing persisted references rather than aborting event loading.
        try { Source = Path.GetFullPath(source); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { Source = source; }
        Title = string.IsNullOrWhiteSpace(title) ? SafeFileName(source) : title;
        if (string.IsNullOrWhiteSpace(Title)) Title = "Referencia sin nombre";
    }

    private static string SafeFileName(string source)
    {
        try { return Path.GetFileNameWithoutExtension(source); }
        catch (ArgumentException) { return source; }
    }
    public void RefreshAvailability() => PropertyChanged?.Invoke(this, new(nameof(IsMissing)));

    private static string SearchFileName(string source)
    {
        try { return Path.GetFileName(source); }
        catch (ArgumentException) { return source; }
    }

    public bool Matches(string? query)
    {
        var term = query?.Trim() ?? "";
        return Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            || SearchFileName(Source).Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    internal void SetDuration(TimeSpan? duration, string status)
    {
        RefreshAvailability();
        Duration = duration;
        DurationStatus = status;
        PropertyChanged?.Invoke(this, new(nameof(Duration)));
        PropertyChanged?.Invoke(this, new(nameof(DurationLabel)));
        PropertyChanged?.Invoke(this, new(nameof(DurationStatus)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
