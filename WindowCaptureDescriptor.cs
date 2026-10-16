namespace EventDeck;

public sealed class WindowCaptureDescriptor
{
    public Guid ResourceId { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Application { get; set; } = "";
}

