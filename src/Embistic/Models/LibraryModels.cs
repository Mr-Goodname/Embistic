namespace Embistic.Models;

public sealed class LibraryItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Artist { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; } = true;
    public int Count { get; set; }
    public string GroupKind { get; set; } = "";
    public string GroupKey { get; set; } = "";
    public IReadOnlyList<string> TrackIds { get; set; } = [];
    public Track? Track { get; set; }
    public string Subtitle => !string.IsNullOrWhiteSpace(Artist) ? Artist : Count > 0 ? $"{Count:N0} 个条目" : Path;
}

public sealed record ListeningStats(
    IReadOnlyList<Track> RecentTracks,
    IReadOnlyList<Track> MostPlayedTracks,
    IReadOnlyList<Album> RecentAlbums,
    IReadOnlyList<Album> MostPlayedAlbums);

public sealed record ListeningHistoryEntry
{
    public string TrackId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string AlbumId { get; init; } = "";
    public string Container { get; init; } = "";
    public long DurationMs { get; init; }
    public int PlayCount { get; init; }
    public long TotalListeningMs { get; init; }
    public DateTimeOffset LastPlayedDate { get; init; }
}
