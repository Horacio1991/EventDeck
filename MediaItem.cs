using System.IO;
using System.Text.Json.Serialization;

namespace EventDeck;

public enum MediaKind { Image, Video, Audio, Web, WindowCapture }

public sealed class MediaItem
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public MediaKind Kind { get; set; }
    public bool ProgramMuted { get; set; }
    public bool Loop { get; set; }
    public WindowCaptureDescriptor? Capture { get; set; }
    [JsonIgnore]
    internal WindowCaptureBinding? CaptureBinding { get; set; }
    [JsonIgnore]
    public bool IsMissing => Kind is MediaKind.Image or MediaKind.Video or MediaKind.Audio && !string.IsNullOrWhiteSpace(Source) && !File.Exists(Source);
    [JsonIgnore]
    public string TypeLabel => Kind switch { MediaKind.Image => "IMAGEN", MediaKind.Video => "VIDEO", MediaKind.Audio => "AUDIO", MediaKind.WindowCapture => "CAPTURA", _ => "WEB" };
    [JsonIgnore]
    public string Glyph => Kind switch { MediaKind.Image => "▧", MediaKind.Video => "▶", MediaKind.Audio => "♫", MediaKind.WindowCapture => "▣", _ => "◎" };
    public override string ToString() => $"{Glyph}   {Name}   ·   {TypeLabel}";
}
