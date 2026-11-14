using System.Text.Json.Serialization;

namespace Embistic.Models;

public sealed record LocalPlaylist
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "播放列表";
    public List<PlaylistTrack> Tracks { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonIgnore] public string DisplayName => $"{Name} · {Tracks.Count:N0} 首";
}

/// <summary>Only server item metadata is persisted; authenticated URLs and local paths are deliberately absent.</summary>
public sealed record PlaylistTrack
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string AlbumId { get; init; } = "";
    public long DurationMs { get; init; }
    public string Container { get; init; } = "";
    public int? SampleRate { get; init; }
    public int? BitDepth { get; init; }
    public static bool CanSave(Track track) => IsItemId(track.Id);
    internal static bool IsItemId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256
        && !id.Any(c => char.IsControl(c) || c is ':' or '/' or '\\');
    public static PlaylistTrack FromTrack(Track track) => new()
    {
        Id = track.Id, Title = Clip(track.Title), Artist = Clip(track.Artist), Album = Clip(track.Album),
        AlbumId = IsItemId(track.AlbumId) ? track.AlbumId : "", DurationMs = Math.Clamp(track.DurationMs, 0, 604800000),
        Container = Clip(track.Container, 40), SampleRate = track.SampleRate, BitDepth = track.BitDepth
    };
    public Track ToTrack() => new() { Id = Id, Title = Title, Artist = Artist, Album = Album, AlbumId = AlbumId,
        DurationMs = DurationMs, Container = Container, SampleRate = SampleRate, BitDepth = BitDepth };
    private static string Clip(string? value, int length = 1000) => string.IsNullOrEmpty(value) ? "" : value[..Math.Min(value.Length, length)];
}
