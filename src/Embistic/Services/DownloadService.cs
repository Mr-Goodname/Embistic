using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Embistic.Models;

namespace Embistic.Services;

public sealed class DownloadService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string _directory;
    private readonly string _accountKey;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public DownloadService(AppSettings settings, string? dataDirectory = null)
    {
        _accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ServerUrl.TrimEnd('/') + "|" + settings.UserId)))[..20];
        _directory = Path.Combine(dataDirectory ?? (string.IsNullOrWhiteSpace(settings.DownloadDirectory) ? DefaultRoot : settings.DownloadDirectory), _accountKey);
    }
    public static string DefaultRoot => Path.Combine(SettingsService.DataDirectory, "downloads");
    public string DirectoryPath => _directory;
    private string ManifestPath => Path.Combine(_directory, "index.json");
    public IReadOnlyList<Track> GetTracks()
    {
        try { return (JsonSerializer.Deserialize<List<Track>>(File.ReadAllText(ManifestPath)) ?? new()).Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Id) && File.Exists(GetLocalPath(t))).ToList(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return Array.Empty<Track>(); }
    }
    public string GetLocalPath(Track track)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(track.Id)))[..20];
        var extension = new string((track.Container ?? "").Where(char.IsLetterOrDigit).Take(12).ToArray());
        return Path.Combine(_directory, id + "." + (extension.Length > 0 ? extension : "audio"));
    }
    public bool IsDownloaded(Track track) => File.Exists(GetLocalPath(track));
    public async Task ChangeDirectoryAsync(string root, CancellationToken ct = default)
    {
        var destination = Path.Combine(Path.GetFullPath(root), _accountKey);
        await _gate.WaitAsync(ct);
        string? manifestTemp = null;
        try
        {
            if (string.Equals(_directory, destination, StringComparison.OrdinalIgnoreCase)) return;
            var tracks = GetTracks();
            Directory.CreateDirectory(destination);
            foreach (var track in tracks)
            {
                ct.ThrowIfCancellationRequested();
                var source = GetLocalPath(track);
                var target = Path.Combine(destination, Path.GetFileName(source));
                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length) continue;
                var temp = target + ".copying";
                try
                {
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                    await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                        await input.CopyToAsync(output, ct);
                    File.Move(temp, target, true);
                }
                finally { TryDelete(temp); }
            }
            // Commit the new index before changing the live playback lookup.
            var manifest = Path.Combine(destination, "index.json");
            var existing = new List<Track>();
            try { if (File.Exists(manifest)) existing = JsonSerializer.Deserialize<List<Track>>(await File.ReadAllTextAsync(manifest, ct)) ?? new(); }
            catch (JsonException) { }
            var merged = existing.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Id)).Concat(tracks).GroupBy(t => t.Id).Select(group => group.Last()).ToList();
            manifestTemp = manifest + ".tmp";
            await File.WriteAllTextAsync(manifestTemp, JsonSerializer.Serialize(merged), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(manifestTemp, manifest, true);
            manifestTemp = null;
            _directory = destination;
        }
        finally { if (manifestTemp is not null) TryDelete(manifestTemp); _gate.Release(); }
    }
    public async Task<string> DownloadAsync(Track track, string streamUrl, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        string? temp = null, manifestTemp = null, publishedFile = null;
        try
        {
            Directory.CreateDirectory(_directory);
            var localPath = GetLocalPath(track);
            if (IsDownloaded(track))
            {
                // Recover a complete audio file left without an index by an older
                // interrupted commit. Reuse its bytes and repair offline browsing.
                if (!GetTracks().Any(existing => existing.Id == track.Id))
                {
                    manifestTemp = ManifestPath + ".tmp";
                    await File.WriteAllTextAsync(manifestTemp, JsonSerializer.Serialize(WithTrack(track)), ct);
                    ct.ThrowIfCancellationRequested();
                    File.Move(manifestTemp, ManifestPath, true); manifestTemp = null;
                }
                return localPath;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(20));
            using var response = await _http.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("下载失败，请检查 Emby 连接。");
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.Contains("html", StringComparison.OrdinalIgnoreCase)
                || type.Contains("xml", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("服务器没有返回音频文件。");
            temp = localPath + ".part";
            var length = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long done = 0; int read;
                while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                    done += read; if (length is > 0) progress?.Report(done * 100.0 / length.Value);
                }
                if (done == 0 || (length is > 0 && done != length)) throw new IOException("音频下载未完成，请重试。");
            }
            // Prepare the index while the audio is still a partial file. Once both
            // writes succeed, commit without another cancellable await between them.
            manifestTemp = ManifestPath + ".tmp";
            await File.WriteAllTextAsync(manifestTemp, JsonSerializer.Serialize(WithTrack(track)), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, localPath, true); temp = null; publishedFile = localPath;
            File.Move(manifestTemp, ManifestPath, true); manifestTemp = null; publishedFile = null;
            return localPath;
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
            if (manifestTemp is not null) TryDelete(manifestTemp);
            if (publishedFile is not null) TryDelete(publishedFile);
            _gate.Release();
        }
    }
    private List<Track> WithTrack(Track track)
    {
        var tracks = GetTracks().Where(existing => existing.Id != track.Id).ToList();
        tracks.Add(new Track { Id = track.Id, Title = track.Title, Artist = track.Artist, Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs, Container = track.Container, BitRate = track.BitRate, SampleRate = track.SampleRate, BitDepth = track.BitDepth, IsFavorite = track.IsFavorite });
        return tracks;
    }
    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Remove(Track track)
    {
        File.Delete(GetLocalPath(track));
        if (File.Exists(ManifestPath)) File.WriteAllText(ManifestPath, JsonSerializer.Serialize(GetTracks().Where(t => t.Id != track.Id)));
    }
    public void Dispose() => _http.Dispose();
}
