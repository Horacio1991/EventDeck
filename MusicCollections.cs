using System.Collections.ObjectModel;

namespace EventDeck;

/// <summary>Event-owned organization, independent of MusicSession and audiovisual bins.</summary>
public sealed class MusicCollections : IDisposable
{
    private readonly ObservableCollection<MusicCollection> _items = new();
    public ReadOnlyObservableCollection<MusicCollection> Items { get; }
    public MusicCollection Active { get; private set; }

    public MusicCollections()
    {
        Items = new(_items);
        Active = new MusicCollection("Música");
        _items.Add(Active);
    }
    public void Select(MusicCollection collection)
    {
        if (_items.Contains(collection)) Active = collection;
    }
    public MusicCollection Create(string name)
    {
        var collection = new MusicCollection(name);
        _items.Add(collection);
        Active = collection;
        return collection;
    }
    public void Remove(MusicCollection collection)
    {
        var index = _items.IndexOf(collection);
        if (index < 0) return;
        _items.RemoveAt(index);
        collection.Dispose(); // Metadata readers only; music playback stays owned by MusicSession.
        if (_items.Count == 0) _items.Add(new MusicCollection("Música"));
        if (ReferenceEquals(Active, collection)) Active = _items[Math.Min(index, _items.Count - 1)];
    }
    public MusicProjectData Capture() => new()
    {
        ActiveCollectionId = Active.Id,
        Collections = _items.Select(c => new MusicCollectionData
        {
            Id = c.Id, Name = c.Name,
            Entries = c.Library.Entries.Select(e => new MusicEntryData
            { Source = e.Source, Title = e.Title, DurationSeconds = e.Duration?.TotalSeconds }).ToList()
        }).ToList()
    };
    public void Restore(MusicProjectData? data)
    {
        // Construct first so metadata/path failures never partially replace the event library.
        var loaded = new List<MusicCollection>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data?.Collections ?? [])
        {
            if (item is null) continue;
            var id = string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) ? Guid.NewGuid().ToString("N") : item.Id;
            ids.Add(id);
            var collection = new MusicCollection(string.IsNullOrWhiteSpace(item.Name) ? "Música" : item.Name, id);
            collection.Library.Restore(item.Entries);
            loaded.Add(collection);
        }
        if (loaded.Count == 0) loaded.Add(new MusicCollection("Música"));
        foreach (var old in _items) old.Dispose();
        _items.Clear();
        foreach (var item in loaded) _items.Add(item);
        Active = _items.FirstOrDefault(c => c.Id == data?.ActiveCollectionId) ?? _items[0];
    }
    public void Dispose() { foreach (var collection in _items) collection.Dispose(); }
}

// Additive DTO nested under EventProject.Music. No runtime/player properties are persisted.
public sealed class MusicProjectData
{
    public string? ActiveCollectionId { get; set; }
    public List<MusicCollectionData>? Collections { get; set; } = new();
}
public sealed class MusicCollectionData
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<MusicEntryData>? Entries { get; set; } = new();
}
public sealed class MusicEntryData
{
    public string? Source { get; set; }
    public string? Title { get; set; }
    public double? DurationSeconds { get; set; }
}
