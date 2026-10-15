using System.Text.Json.Serialization;

namespace EventDeck;

public enum MediaKind { Image, Video, Audio, Web }

public sealed class MediaItem
{
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public MediaKind Kind { get; set; }
    public bool ProgramMuted { get; set; }
    public bool Loop { get; set; }
    [JsonIgnore]
    public string TypeLabel => Kind switch { MediaKind.Image => "IMAGEN", MediaKind.Video => "VIDEO", MediaKind.Audio => "AUDIO", _ => "WEB" };
    [JsonIgnore]
    public string Glyph => Kind switch { MediaKind.Image => "▧", MediaKind.Video => "▶", MediaKind.Audio => "♫", _ => "◎" };
    public override string ToString() => $"{Glyph}   {Name}   ·   {TypeLabel}";
}
