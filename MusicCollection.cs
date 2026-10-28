using System.ComponentModel;

namespace EventDeck;

/// <summary>Organization only: a collection never owns a playback session.</summary>
public sealed class MusicCollection : INotifyPropertyChanged, IDisposable
{
    private string _name;
    public string Id { get; }
    public string Name
    {
        get => _name;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Escribí un nombre para la colección.");
            _name = value.Trim();
            PropertyChanged?.Invoke(this, new(nameof(Name)));
        }
    }
    public MusicLibrary Library { get; } = new();
    public bool IsDeleted { get; private set; }
    public MusicCollection(string name, string? id = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Escribí un nombre para la colección.");
        _name = name.Trim();
        Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    }
    public void Dispose() { IsDeleted = true; Library.Dispose(); }
    public event PropertyChangedEventHandler? PropertyChanged;
}
