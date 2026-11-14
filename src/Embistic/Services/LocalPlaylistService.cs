using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Account-scoped manual lists. Writes replace an entire file atomically and publish memory only after success.</summary>
public sealed class LocalPlaylistService
{
    private readonly object _gate = new();
    private List<LocalPlaylist> _lists = [];
    public string StoragePath { get; }
    public string? LoadWarning { get; private set; }
    public LocalPlaylistService(AppSettings settings, string? directory = null)
    {
        var server = (settings.ServerUrl ?? "").Trim().TrimEnd('/');
        if (Uri.TryCreate(server, UriKind.Absolute, out var uri)) server = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        var account = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server + "|" + (settings.UserId ?? ""))))[..24].ToLowerInvariant();
        StoragePath = Path.Combine(directory ?? Path.Combine(SettingsService.DataDirectory, "playlists"), account + ".json");
        try
        {
            if (!File.Exists(StoragePath)) return;
            if (new FileInfo(StoragePath).Length > 32 * 1024 * 1024) throw new InvalidDataException("播放列表文件过大。");
            var loaded = JsonSerializer.Deserialize<List<LocalPlaylist>>(File.ReadAllText(StoragePath)) ?? throw new InvalidDataException("播放列表格式无效。");
            if (loaded.Count > 200 || loaded.Any(x => x is null || string.IsNullOrWhiteSpace(x.Id) || x.Tracks is null || x.Tracks.Count > 10000))
                throw new InvalidDataException("播放列表格式无效。");
            _lists = loaded.Select(x => x with { Name = ValidName(x.Name), Tracks = x.Tracks.Where(t => t is not null && PlaylistTrack.IsItemId(t.Id)).Select(t => PlaylistTrack.FromTrack(t.ToTrack())).ToList() }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { LoadWarning = "无法读取本地播放列表，原文件已保留。"; }
    }
    public IReadOnlyList<LocalPlaylist> GetAll()
    {
        lock (_gate) return _lists.Select(Clone).OrderByDescending(x => x.UpdatedAt).ToArray();
    }
    public LocalPlaylist Create(string name, IEnumerable<Track> tracks)
    {
        lock (_gate)
        {
            if (_lists.Count >= 200) throw new InvalidOperationException("最多保存 200 个播放列表。");
            name = UniqueName(name, null);
            var list = new LocalPlaylist { Name = name, Tracks = Snapshot(tracks) };
            Commit([.._lists, list]); return Clone(list);
        }
    }
    public void Rename(string id, string name) => Change(id, list => list with { Name = UniqueName(name, id) });
    public void ReplaceTracks(string id, IEnumerable<Track> tracks)
    {
        var snapshot = Snapshot(tracks);
        Change(id, list => list with { Tracks = snapshot });
    }
    public void AddTracks(string id, IEnumerable<Track> tracks)
    {
        var snapshot = Snapshot(tracks);
        Change(id, list => list.Tracks.Count + snapshot.Count > 10000 ? throw new InvalidOperationException("每个列表最多保存 10,000 首歌曲。") : list with { Tracks = [..list.Tracks, ..snapshot] });
    }
    public void Delete(string id)
    {
        lock (_gate) { if (_lists.All(x => x.Id != id)) return; Commit(_lists.Where(x => x.Id != id).ToList()); }
    }
    private void Change(string id, Func<LocalPlaylist, LocalPlaylist> change)
    {
        lock (_gate)
        {
            var index = _lists.FindIndex(x => x.Id == id);
            if (index < 0) throw new InvalidOperationException("播放列表已不存在。");
            var next = _lists.ToList(); next[index] = change(next[index]) with { UpdatedAt = DateTimeOffset.UtcNow }; Commit(next);
        }
    }
    private void Commit(List<LocalPlaylist> lists)
    {
        if (LoadWarning is not null) throw new InvalidOperationException(LoadWarning);
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var payload = JsonSerializer.SerializeToUtf8Bytes(lists);
        if (payload.Length > 32 * 1024 * 1024) throw new InvalidOperationException("本地播放列表总容量已达上限，请减少曲目后再保存。");
        var temporary = StoragePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, payload);
            File.Move(temporary, StoragePath, true); _lists = lists;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private string UniqueName(string name, string? except)
    {
        name = ValidName(name);
        if (_lists.Any(x => x.Id != except && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new ArgumentException("已有同名播放列表，请使用另一个名称。");
        return name;
    }
    private static string ValidName(string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl)) throw new ArgumentException("请输入 1–120 字的播放列表名称。");
        return name;
    }
    private static List<PlaylistTrack> Snapshot(IEnumerable<Track> tracks)
    {
        var result = tracks.Where(PlaylistTrack.CanSave).Take(10001).Select(PlaylistTrack.FromTrack).ToList();
        if (result.Count > 10000) throw new InvalidOperationException("每个列表最多保存 10,000 首歌曲。");
        return result;
    }
    private static LocalPlaylist Clone(LocalPlaylist list) => list with { Tracks = list.Tracks.Select(x => x with { }).ToList() };
}
