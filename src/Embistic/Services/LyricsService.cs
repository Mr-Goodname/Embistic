using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Emby lyrics, a local cache, LRCLIB, and public NetEase lyric metadata.</summary>
public sealed class LyricsService
{
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Regex Timestamp = new(@"\[(?:(?<hour>\d{1,2}):)?(?<minute>\d{1,3}):(?<second>\d{1,2})(?:[.:](?<fraction>\d{1,3}))?\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Offset = new(@"\[offset\s*:\s*(?<value>[+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex EnhancedTimestamp = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Metadata = new(@"^\s*(?:\[(?:ar|al|ti|au|by|re|ve|length|id|tool|offset)\s*:[^\]]*\]\s*)+$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private readonly string _cacheDirectory;
    private NetworkLyricsSource[] _sources = Array.Empty<NetworkLyricsSource>();
    public void ConfigureSources(IEnumerable<NetworkLyricsSource> sources) => _sources = sources.Where(source => source.Enabled).Take(16).Select(source => source.Clone()).ToArray();

    public LyricsService(string? cacheDirectory = null)
    {
        _cacheDirectory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Embistic", "lyrics");
    }

    public async Task<LyricsDocument> GetLyricsAsync(Track track, string? serverLyrics = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        cancellationToken.ThrowIfCancellationRequested();
        // An explicit correction belongs to the user and must survive future server/network matches.
        var imported = await GetImportedLyricsAsync(track, cancellationToken).ConfigureAwait(false);
        if (imported is not null) return imported;
        if (!string.IsNullOrWhiteSpace(serverLyrics))
        {
            var fromServer = ParseLrc(serverLyrics, "Emby");
            if (fromServer.IsSynced) return fromServer;
            // A plain server lyric remains a useful fallback if a timed version is unavailable.
            var network = await FindLyricsAsync(track, cancellationToken).ConfigureAwait(false);
            return network.IsSynced || network.IsInstrumental ? network : fromServer;
        }
        return await FindLyricsAsync(track, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LyricsDocument> ImportLyricsAsync(Track track, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("歌词文件为空。", nameof(text));
        if (text.Length > 2_000_000) throw new ArgumentException("歌词文件过大，请选择小于 2 MB 的文本文件。", nameof(text));
        var document = ParseLrc(text, "本地 LRC");
        if (string.IsNullOrWhiteSpace(document.PlainText)) throw new ArgumentException("文件中没有可识别的歌词文本。", nameof(text));
        var path = Path.Combine(_cacheDirectory, "overrides", GetCacheKey(track) + ".lrc");
        await WriteAtomicAsync(path, text, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>Allows the caller to avoid even a server request for a manually imported lyric.</summary>
    public async Task<LyricsDocument?> GetImportedLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var path = Path.Combine(_cacheDirectory, "overrides", GetCacheKey(track) + ".lrc");
        try
        {
            if (!File.Exists(path)) return null;
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var document = ParseLrc(text, "本地 LRC");
            return !string.IsNullOrWhiteSpace(document.PlainText) ? document : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static LyricsDocument ParseLrc(string text, string source = "LRC") => LyricsParser.Parse(text, source);

    public static int FindLineIndex(LyricsDocument document, long positionMs, double offsetMs = 0)
    {
        // Positive manual offset advances the displayed lyric; useful for a lagging lyric file.
        var target = positionMs + offsetMs;
        var low = 0;
        var high = document.Lines.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (document.Lines[middle].Time.TotalMilliseconds <= target) { found = middle; low = middle + 1; }
            else high = middle - 1;
        }
        return found;
    }

    private async Task<LyricsDocument> FindLyricsAsync(Track track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Title) || string.IsNullOrWhiteSpace(track.Artist))
            return LyricsDocument.Empty("歌曲信息不足，无法匹配歌词");
        var sources = _sources;
        var sourceKey = sources.Length == 0 ? "" : "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sources))))[..20];
        var cachePath = Path.Combine(_cacheDirectory, GetCacheKey(track) + sourceKey + ".json");
        try
        {
            if (File.Exists(cachePath))
            {
                var cached = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false));
                if (cached?.Document is { } document && DateTimeOffset.UtcNow - cached.CreatedUtc < TimeSpan.FromDays(30))
                    return document;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }

        var result = LyricsDocument.Empty();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = await NetworkLyricsProvider.FetchAsync(source, track, cancellationToken).ConfigureAwait(false);
            if (candidate.IsSynced || candidate.IsInstrumental)
            {
                try { await WriteAtomicAsync(cachePath, JsonSerializer.Serialize(new CacheEntry { CreatedUtc = DateTimeOffset.UtcNow, Document = candidate }), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return candidate;
            }
            if (!string.IsNullOrWhiteSpace(candidate.Source) && !string.IsNullOrWhiteSpace(candidate.PlainText)) result = candidate;
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        // The first accurate timed result wins; a slow catalogue cannot delay a usable one.
        var pending = new List<Task<LyricsDocument>>
        {
            SafeProviderAsync(() => FetchLrclibAsync(track, bounded.Token)),
            SafeProviderAsync(() => FetchNetEaseAsync(track, bounded.Token))
        };
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            var candidate = await completed.ConfigureAwait(false);
            if (candidate.IsSynced || candidate.IsInstrumental)
            {
                result = candidate;
                bounded.Cancel();
                // Observe canceled requests, releasing their response streams before returning.
                await Task.WhenAll(pending).ConfigureAwait(false);
                break;
            }
            if (!string.IsNullOrWhiteSpace(candidate.Source) || string.IsNullOrWhiteSpace(result.Source)) result = candidate;
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (result.IsSynced || result.IsInstrumental || !string.IsNullOrWhiteSpace(result.Source))
        {
            try
            {
                var entry = new CacheEntry { CreatedUtc = DateTimeOffset.UtcNow, Document = result };
                await WriteAtomicAsync(cachePath, JsonSerializer.Serialize(entry), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static string GetCacheKey(Track track)
    {
        var fingerprint = $"{Normalize(track.Title)}\n{Normalize(track.Artist)}\n{Normalize(track.Album)}\n{track.DurationMs / 1000}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
    }

    private static async Task WriteAtomicAsync(string path, string text, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<LyricsDocument> SafeProviderAsync(Func<Task<LyricsDocument>> provider)
    {
        try { return await provider().ConfigureAwait(false); }
        catch (OperationCanceledException) { return LyricsDocument.Empty("歌词获取超时，可稍后重试"); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        { return LyricsDocument.Empty("歌词服务暂时不可用"); }
    }

    private static async Task<LyricsDocument> FetchLrclibAsync(Track track, CancellationToken cancellationToken)
    {
        var query = $"track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(track.Artist)}";
        if (!string.IsNullOrWhiteSpace(track.Album)) query += "&album_name=" + Uri.EscapeDataString(track.Album);
        if (track.DurationMs > 0) query += "&duration=" + (track.DurationMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture);
        Candidate? best = null;
        using var exact = await RequestAsync("https://lrclib.net/api/get?" + query, cancellationToken).ConfigureAwait(false);
        if (exact is not null)
        {
            var candidate = ReadCandidate(exact.RootElement);
            if (candidate is not null && MatchScore(track, candidate) >= 100) best = candidate;
        }
        if (best is null || !best.HasSyncedLyrics)
        {
            var searchQuery = $"track_name={Uri.EscapeDataString(track.Title)}&artist_name={Uri.EscapeDataString(track.Artist)}";
            using var search = await RequestAsync("https://lrclib.net/api/search?" + searchQuery, cancellationToken).ConfigureAwait(false);
            if (search is not null && search.RootElement.ValueKind == JsonValueKind.Array)
            {
                var match = search.RootElement.EnumerateArray().Take(100).Select(ReadCandidate).Where(x => x is not null)
                    .Select(x => new { Candidate = x!, Score = MatchScore(track, x!) })
                    .Where(x => x.Score >= 100).OrderByDescending(x => x.Candidate.HasSyncedLyrics).ThenByDescending(x => x.Score).FirstOrDefault();
                if (match is not null && (best is null || match.Candidate.HasSyncedLyrics || MatchScore(track, best) < match.Score)) best = match.Candidate;
            }
        }
        return best switch
        {
            { Instrumental: true } => new LyricsDocument { IsInstrumental = true, PlainText = "纯音乐，请欣赏", Source = "LRCLIB" },
            { HasSyncedLyrics: true } => ParseLrc(best.SyncedLyrics!, "LRCLIB"),
            { PlainLyrics.Length: > 0 } => ParseLrc(best.PlainLyrics, "LRCLIB"),
            _ => LyricsDocument.Empty()
        };
    }

    private static async Task<LyricsDocument> FetchNetEaseAsync(Track track, CancellationToken cancellationToken)
    {
        // Public, unauthenticated search and lyric endpoints; no media or paid content retrieval.
        var searchTerm = Uri.EscapeDataString(track.Title + " " + track.Artist);
        using var search = await RequestAsync($"https://music.163.com/api/search/get?s={searchTerm}&type=1&limit=20", cancellationToken).ConfigureAwait(false);
        if (search is null || !search.RootElement.TryGetProperty("result", out var result)
            || !result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return LyricsDocument.Empty();
        var candidates = new List<(long Id, Candidate Metadata, int Score)>();
        foreach (var song in songs.EnumerateArray().Take(20))
        {
            if (!song.TryGetProperty("id", out var id) || !id.TryGetInt64(out var songId)) continue;
            var title = ReadString(song, "name");
            var artist = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Join("、", artists.EnumerateArray().Select(x => ReadString(x, "name"))) : "";
            var album = song.TryGetProperty("album", out var albumValue) ? ReadString(albumValue, "name") : "";
            var duration = song.TryGetProperty("duration", out var durationValue) && durationValue.TryGetDouble(out var ms) ? ms / 1000 : 0;
            var metadata = new Candidate(title, artist, album, duration, false, null, "");
            var score = MatchScore(track, metadata);
            if (score >= 100) candidates.Add((songId, metadata, score));
        }
        foreach (var candidate in candidates.OrderByDescending(x => x.Score).Take(2))
        {
            using var lyrics = await RequestAsync($"https://music.163.com/api/song/lyric?id={candidate.Id}&lv=-1&kv=-1&tv=-1", cancellationToken).ConfigureAwait(false);
            if (lyrics is null) continue;
            var root = lyrics.RootElement;
            var original = root.TryGetProperty("lrc", out var lrc) ? ReadString(lrc, "lyric") : "";
            if (string.IsNullOrWhiteSpace(original)) continue;
            if (original.Contains("纯音乐，请欣赏", StringComparison.Ordinal))
                return new LyricsDocument { IsInstrumental = true, PlainText = "纯音乐，请欣赏", Source = "网易云音乐" };
            var translation = root.TryGetProperty("tlyric", out var tlyric) ? ReadString(tlyric, "lyric") : "";
            return ParseLrc(original + (string.IsNullOrWhiteSpace(translation) ? "" : "\n" + translation), "网易云音乐");
        }
        return LyricsDocument.Empty();
    }

    private static string ReadString(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static async Task<JsonDocument?> RequestAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return null;
        response.EnsureSuccessStatusCode();
        // A catalogue response is small; reject an unexpected oversized response.
        if (response.Content.Headers.ContentLength is > 4_000_000) throw new IOException("歌词响应过大");
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        if (bytes.Length > 4_000_000) throw new IOException("歌词响应过大");
        return JsonDocument.Parse(bytes);
    }

    internal static int MatchScore(Track track, Candidate candidate)
    {
        if (Normalize(track.Title) != Normalize(candidate.Title)) return 0;
        var artist = Normalize(track.Artist);
        var candidateArtist = Normalize(candidate.Artist);
        if (artist.Length == 0 || candidateArtist.Length == 0) return 0;
        var artistExact = artist == candidateArtist;
        var artistParts = SplitArtists(track.Artist);
        var candidateParts = SplitArtists(candidate.Artist);
        if (!artistExact && !artistParts.Intersect(candidateParts).Any()) return 0;
        var deltaSeconds = Math.Abs(track.DurationMs / 1000d - candidate.Duration);
        if (track.DurationMs > 0 && (candidate.Duration <= 0 || deltaSeconds > Math.Max(8, Math.Min(15, track.DurationMs / 1000d * .04)))) return 0;
        var albumExact = !string.IsNullOrWhiteSpace(track.Album) && Normalize(track.Album) == Normalize(candidate.Album);
        return 100 + (artistExact ? 20 : 0) + (albumExact ? 10 : 0) + (deltaSeconds < 2 ? 5 : 0);
    }

    private static string[] SplitArtists(string value) => Regex.Split(value, @"\s*(?:,|，|、|;|；|&|／|\s/\s|\sfeat\.?\s|\sfeaturing\s)\s*", RegexOptions.IgnoreCase)
        .Select(Normalize).Where(x => x.Length > 0).ToArray();

    private static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormKC).EnumerateRunes().Where(Rune.IsLetterOrDigit)).ToUpperInvariant();

    private static Candidate? ReadCandidate(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        string ReadString(string key) => value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
        var title = ReadString("trackName");
        if (title.Length == 0) return null;
        return new Candidate(title, ReadString("artistName"), ReadString("albumName"),
            value.TryGetProperty("duration", out var duration) && duration.TryGetDouble(out var number) ? number : 0,
            value.TryGetProperty("instrumental", out var instrumental) && instrumental.ValueKind == JsonValueKind.True,
            ReadString("syncedLyrics"), ReadString("plainLyrics"));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Embistic/0.1 (Windows music player)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    internal sealed record Candidate(string Title, string Artist, string Album, double Duration, bool Instrumental, string? SyncedLyrics, string PlainLyrics)
    {
        public bool HasSyncedLyrics => !string.IsNullOrWhiteSpace(SyncedLyrics);
    }

    private sealed class CacheEntry
    {
        public DateTimeOffset CreatedUtc { get; set; }
        public LyricsDocument? Document { get; set; }
    }
}
