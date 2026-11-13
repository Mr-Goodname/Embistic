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
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public DownloadService(AppSettings settings, string? dataDirectory = null)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ServerUrl.TrimEnd('/') + "|" + settings.UserId)))[..20];
        _directory = Path.Combine(dataDirectory ?? Path.Combine(SettingsService.DataDirectory, "downloads"), key);
    }
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
    public async Task<string> DownloadAsync(Track track, string streamUrl, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        string? temp = null;
        try
        {
            if (IsDownloaded(track)) return GetLocalPath(track);
            Directory.CreateDirectory(_directory);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(20));
            using var response = await _http.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("下载失败，请检查 Emby 连接。");
            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (type.Contains("json") || type.Contains("html") || type.Contains("xml")) throw new InvalidOperationException("服务器没有返回音频文件。");
            temp = GetLocalPath(track) + ".part";
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
            File.Move(temp, GetLocalPath(track), true); temp = null;
            var tracks = GetTracks().Where(t => t.Id != track.Id).ToList();
            tracks.Add(new Track { Id = track.Id, Title = track.Title, Artist = track.Artist, Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs, Container = track.Container, BitRate = track.BitRate, SampleRate = track.SampleRate, BitDepth = track.BitDepth, IsFavorite = track.IsFavorite });
            var manifestTemp = ManifestPath + ".tmp";
            await File.WriteAllTextAsync(manifestTemp, JsonSerializer.Serialize(tracks), ct);
            File.Move(manifestTemp, ManifestPath, true);
            return GetLocalPath(track);
        }
        finally { if (temp is not null) { try { File.Delete(temp); } catch (IOException) { } } _gate.Release(); }
    }
    public void Remove(Track track)
    {
        File.Delete(GetLocalPath(track));
        if (File.Exists(ManifestPath)) File.WriteAllText(ManifestPath, JsonSerializer.Serialize(GetTracks().Where(t => t.Id != track.Id)));
    }
    public void Dispose() => _http.Dispose();
}
