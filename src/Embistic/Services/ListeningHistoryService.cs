using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Counts qualified listening, not clicks or seeks. The store contains metadata but no stream URLs or credentials.</summary>
public sealed class ListeningHistoryService
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly string _path;
    private readonly Dictionary<string, ListeningHistoryEntry> _entries;
    private Track? _current;
    private long _lastTimestamp, _lastPosition;
    private long _lastProgressTimestamp;
    private double _listenedMs;
    private bool _wasPlaying, _counted;

    public event Action? Changed;
    public string StoragePath => _path;

    public ListeningHistoryService(AppSettings settings, string? dataDirectory = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _clock = clock ?? TimeProvider.System;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            (settings.ServerUrl ?? "").Trim().TrimEnd('/') + "|" + (settings.UserId ?? ""))))[..24].ToLowerInvariant();
        var directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Embistic", "history");
        _path = Path.Combine(directory, key + ".json");
        _entries = ReadEntries(_path);
    }

    public IReadOnlyList<ListeningHistoryEntry> GetEntries()
    {
        lock (_gate) return _entries.Values.OrderByDescending(entry => entry.LastPlayedDate).Select(entry => entry with { }).ToArray();
    }

    public void BeginTrack(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate) Begin(track);
    }

    public void UpdatePlayback(Track track, long positionMs, bool isPlaying)
    {
        ArgumentNullException.ThrowIfNull(track);
        bool changed;
        lock (_gate)
        {
            if (_current?.Id != track.Id) Begin(track);
            var now = _clock.GetTimestamp();
            var elapsed = Math.Clamp(_clock.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds, 0, 5000);
            if (positionMs != _lastPosition && positionMs > 0) _lastProgressTimestamp = now;
            var recentlyProgressed = _clock.GetElapsedTime(_lastProgressTimestamp, now) <= TimeSpan.FromSeconds(2);
            if (_wasPlaying && positionMs > 0 && recentlyProgressed) _listenedMs += elapsed;
            _lastTimestamp = now;
            _lastPosition = Math.Max(0, positionMs);
            _wasPlaying = isPlaying;
            changed = Qualify(track);
        }
        if (changed) Changed?.Invoke();
    }

    public void EndTrack(Track track, long positionMs)
    {
        if (track is null) return;
        bool changed;
        lock (_gate)
        {
            if (_current?.Id != track.Id) return;
            var now = _clock.GetTimestamp();
            if (_wasPlaying && positionMs > 0 && _clock.GetElapsedTime(_lastProgressTimestamp, now) <= TimeSpan.FromSeconds(2))
                _listenedMs += Math.Clamp(_clock.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds, 0, 5000);
            changed = Qualify(track);
            _current = null;
            _wasPlaying = false;
        }
        if (changed) Changed?.Invoke();
    }

    private void Begin(Track track)
    {
        _current = track;
        _lastTimestamp = _lastProgressTimestamp = _clock.GetTimestamp();
        _lastPosition = 0;
        _listenedMs = 0;
        _wasPlaying = false;
        _counted = false;
    }

    private bool Qualify(Track track)
    {
        var threshold = track.DurationMs > 0 ? Math.Min(30000, Math.Max(1000, track.DurationMs * .5)) : 30000;
        if (_counted || string.IsNullOrWhiteSpace(track.Id) || _listenedMs < threshold) return false;
        _entries.TryGetValue(track.Id, out var previous);
        _entries[track.Id] = new ListeningHistoryEntry
        {
            TrackId = track.Id, Title = track.Title, Artist = track.Artist, Album = track.Album, AlbumId = track.AlbumId,
            Container = track.Container, DurationMs = Math.Max(0, track.DurationMs),
            PlayCount = previous?.PlayCount == int.MaxValue ? int.MaxValue : (previous?.PlayCount ?? 0) + 1,
            TotalListeningMs = Math.Min(long.MaxValue - 30000, previous?.TotalListeningMs ?? 0) + (long)Math.Min(_listenedMs, 30000),
            LastPlayedDate = _clock.GetUtcNow()
        };
        _counted = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var retained = _entries.Values.OrderByDescending(entry => entry.LastPlayedDate).Take(10000).ToArray();
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(retained), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* The current session still works when the history directory is read-only. */ }
        return true;
    }

    private static Dictionary<string, ListeningHistoryEntry> ReadEntries(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            var entries = JsonSerializer.Deserialize<List<ListeningHistoryEntry>>(File.ReadAllText(path)) ?? [];
            return entries.Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.TrackId) && entry.PlayCount > 0)
                .GroupBy(entry => entry.TrackId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(entry => entry.LastPlayedDate).First(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(StringComparer.Ordinal); }
    }
}
