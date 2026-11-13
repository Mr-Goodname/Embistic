using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Embistic.Models;

namespace Embistic.Services;

public sealed partial class EmbyClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly string _baseUrl;
    private readonly ConcurrentDictionary<string, string> _mediaSourceIds = new();
    private readonly Dictionary<string, string> _playSessionIds = new();
    private static readonly string DeviceId = "embistic-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + Environment.UserName)))[..24].ToLowerInvariant();
    private const string TrackFields = "MediaSources,MediaStreams,AudioInfo,UserData,Artists,Album,AlbumArtists";

    public EmbyClient(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // A client represents one connection. Editing the settings form must not mix
        // credentials or users from a new connection with this client's base URL.
        _settings = new AppSettings
        {
            ServerUrl = settings.ServerUrl ?? "",
            ApiKey = settings.ApiKey ?? "",
            UserId = settings.UserId ?? "",
            UserName = settings.UserName ?? ""
        };
        if (!Uri.TryCreate(_settings.ServerUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请输入有效的 Emby 服务器地址，例如 http://192.168.1.10:8096。", nameof(settings));
        _baseUrl = uri.AbsoluteUri.TrimEnd('/');
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", _settings.ApiKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Authorization", $"MediaBrowser Client=\"Embistic\", Device=\"Windows\", DeviceId=\"{DeviceId}\", Version=\"0.3.1\"");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Embistic/0.3.1");
    }

    public async Task<IReadOnlyList<EmbyUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync("Users", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        return document.RootElement.EnumerateArray()
            .Where(user => !Boolean(Property(user, "Policy"), "IsDisabled"))
            .Select(user => new EmbyUser(String(user, "Id"), String(user, "Name")))
            .Where(user => user.Id.Length > 0).ToList();
    }

    public async Task<PagedResult<Track>> GetTracksAsync(string? search = null, int start = 0, int limit = 200, string? albumId = null, bool favoritesOnly = false, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["IncludeItemTypes"] = "Audio", ["Recursive"] = "true", ["StartIndex"] = Math.Max(0, start).ToString(CultureInfo.InvariantCulture),
            ["Limit"] = Math.Clamp(limit, 1, 10000).ToString(CultureInfo.InvariantCulture), ["Fields"] = TrackFields,
            ["SortBy"] = string.IsNullOrWhiteSpace(albumId) ? "SortName" : "ParentIndexNumber,IndexNumber,SortName", ["SortOrder"] = "Ascending",
            ["SearchTerm"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(), ["ParentId"] = albumId,
            ["Filters"] = favoritesOnly ? "IsFavorite" : null
        };
        using var document = await GetJsonAsync(ItemsPath + Query(query), cancellationToken);
        var items = Array(document.RootElement, "Items").Select(ParseTrack).ToList();
        return new PagedResult<Track>(items, Integer(document.RootElement, "TotalRecordCount") ?? items.Count);
    }

    public async Task<IReadOnlyList<Album>> GetAlbumsAsync(bool favoritesOnly = false, int limit = 24, string? search = null, int start = 0, CancellationToken cancellationToken = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["IncludeItemTypes"] = "MusicAlbum", ["Recursive"] = "true", ["Limit"] = Math.Clamp(limit, 1, 2000).ToString(CultureInfo.InvariantCulture),
            ["StartIndex"] = Math.Max(0, start).ToString(CultureInfo.InvariantCulture), ["Fields"] = "Artists,AlbumArtists,UserData,DateCreated",
            ["SortBy"] = "DateCreated,SortName", ["SortOrder"] = "Descending", ["SearchTerm"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            ["Filters"] = favoritesOnly ? "IsFavorite" : null
        };
        using var document = await GetJsonAsync(ItemsPath + Query(query), cancellationToken);
        return Array(document.RootElement, "Items").Select(item => new Album
        {
            Id = String(item, "Id"), Title = String(item, "Name"), Artist = Artist(item), Year = Integer(item, "ProductionYear"),
            IsFavorite = Boolean(Property(item, "UserData"), "IsFavorite"), ImageUrl = ImageFor(item)
        }).ToList();
    }

    public async Task<int> GetAlbumCountAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(ItemsPath + "?IncludeItemTypes=MusicAlbum&Recursive=true&Limit=0", cancellationToken);
        return Integer(document.RootElement, "TotalRecordCount") ?? 0;
    }

    public string GetStreamUrl(Track track)
    {
        var query = new Dictionary<string, string?> { ["static"] = "true", ["api_key"] = _settings.ApiKey, ["UserId"] = _settings.UserId, ["DeviceId"] = DeviceId };
        if (_mediaSourceIds.TryGetValue(track.Id, out var sourceId)) query["MediaSourceId"] = sourceId;
        return _baseUrl + "/Audio/" + Uri.EscapeDataString(track.Id) + "/stream" + Query(query);
    }

    public string GetImageUrl(string id, int size = 400) => string.IsNullOrWhiteSpace(id) ? "" : _baseUrl + "/Items/" + Uri.EscapeDataString(id) + "/Images/Primary" + Query(new Dictionary<string, string?> { ["maxWidth"] = Math.Clamp(size, 32, 1600).ToString(CultureInfo.InvariantCulture), ["quality"] = "90", ["api_key"] = _settings.ApiKey });

    public async Task SetFavoriteAsync(Track track, bool favorite, CancellationToken cancellationToken = default)
    {
        RequireUser();
        var path = $"Users/{Uri.EscapeDataString(_settings.UserId)}/FavoriteItems/{Uri.EscapeDataString(track.Id)}";
        using var request = new HttpRequestMessage(favorite ? HttpMethod.Post : HttpMethod.Delete, path);
        using var response = await _http.SendAsync(request, cancellationToken);
        EnsureSuccess(response);
        track.IsFavorite = favorite;
    }

    public async Task ReportPlaybackAsync(Track track, long positionMs, bool paused, string eventName, CancellationToken cancellationToken = default)
    {
        string path = eventName.ToLowerInvariant() switch { "start" => "Sessions/Playing", "progress" => "Sessions/Playing/Progress", "stop" => "Sessions/Playing/Stopped", _ => throw new ArgumentException("Unknown playback event.", nameof(eventName)) };
        string sessionId;
        lock (_playSessionIds)
        {
            if (eventName.Equals("start", StringComparison.OrdinalIgnoreCase) || !_playSessionIds.TryGetValue(track.Id, out sessionId!))
                _playSessionIds[track.Id] = sessionId = Guid.NewGuid().ToString("N");
        }
        _mediaSourceIds.TryGetValue(track.Id, out var mediaSourceId);
        var payload = new
        {
            ItemId = track.Id, UserId = _settings.UserId, MediaSourceId = mediaSourceId ?? track.Id,
            PlaySessionId = sessionId, PositionTicks = Math.Clamp(positionMs, 0, long.MaxValue / TimeSpan.TicksPerMillisecond) * TimeSpan.TicksPerMillisecond,
            IsPaused = paused, IsMuted = false, CanSeek = true, PlayMethod = "DirectPlay", RepeatMode = "RepeatNone", EventName = "timeupdate"
        };
        using var response = await _http.PostAsJsonAsync(path, payload, cancellationToken);
        EnsureSuccess(response);
        if (eventName.Equals("stop", StringComparison.OrdinalIgnoreCase)) lock (_playSessionIds) _playSessionIds.Remove(track.Id);
    }

    public async Task<string?> GetServerLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        // Recent Emby/Jellyfin versions expose lyrics here; older servers may not implement it.
        using var response = await _http.GetAsync($"Audio/{Uri.EscapeDataString(track.Id)}/Lyrics", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(content)) return null;
        if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            return content.Contains('<') && content.Contains("html", StringComparison.OrdinalIgnoreCase) ? null : content;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return root.GetString();
            foreach (var key in new[] { "SyncedLyrics", "syncedLyrics", "Lrc", "lrc", "Text", "text" })
                if (String(root, key) is { Length: > 0 } text) return text;
            var lyrics = Array(root, "Lyrics").ToList();
            if (lyrics.Count == 0) lyrics = Array(root, "lyrics").ToList();
            if (lyrics.Count == 0) return null;
            return string.Join('\n', lyrics.Select(line =>
            {
                var text = String(line, "Text");
                if (text.Length == 0) text = String(line, "text");
                var ticks = Long(line, "Start") ?? Long(line, "start");
                if (ticks is null) return text;
                var time = TimeSpan.FromTicks(Math.Max(0, ticks.Value));
                return $"[{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}]{text}";
            }));
        }
        catch (JsonException) { return null; }
    }

    private string ItemsPath => string.IsNullOrWhiteSpace(_settings.UserId) ? "Items" : $"Users/{Uri.EscapeDataString(_settings.UserId)}/Items";

    private Track ParseTrack(JsonElement item)
    {
        var source = Array(item, "MediaSources").FirstOrDefault();
        var stream = Array(item, "MediaStreams").FirstOrDefault(x => String(x, "Type") == "Audio");
        if (stream.ValueKind == JsonValueKind.Undefined) stream = Array(source, "MediaStreams").FirstOrDefault(x => String(x, "Type") == "Audio");
        var id = String(item, "Id");
        var sourceId = String(source, "Id");
        if (sourceId.Length > 0) _mediaSourceIds[id] = sourceId;
        return new Track
        {
            Id = id, Title = String(item, "Name"), Artist = Artist(item), Album = String(item, "Album"), AlbumId = String(item, "AlbumId"),
            DurationMs = Math.Max(0, Long(item, "RunTimeTicks") ?? 0) / TimeSpan.TicksPerMillisecond,
            Container = String(stream, "Codec") is { Length: > 0 } codec ? codec : String(item, "Container"),
            BitRate = Integer(stream, "BitRate") ?? Integer(item, "Bitrate"), SampleRate = Integer(stream, "SampleRate"), BitDepth = Integer(stream, "BitDepth"),
            Index = Integer(item, "IndexNumber") ?? 0, IsFavorite = Boolean(Property(item, "UserData"), "IsFavorite"), ImageUrl = ImageFor(item)
        };
    }

    private string ImageFor(JsonElement item)
    {
        if (String(Property(item, "ImageTags"), "Primary").Length > 0) return GetImageUrl(String(item, "Id"));
        if (String(item, "AlbumId") is { Length: > 0 } albumId) return GetImageUrl(albumId);
        if (String(item, "PrimaryImageItemId") is { Length: > 0 } primaryId) return GetImageUrl(primaryId);
        return "";
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        EnsureSuccess(response);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Emby 拒绝访问，请检查 API 密钥和用户权限。",
            HttpStatusCode.NotFound => "Emby 找不到该资源，音乐可能已移动或删除。",
            _ => $"Emby 请求失败（HTTP {(int)response.StatusCode}）。"
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private void RequireUser() { if (string.IsNullOrWhiteSpace(_settings.UserId)) throw new InvalidOperationException("请先选择 Emby 用户。"); }
    private static string Query(IDictionary<string, string?> values) => "?" + string.Join('&', values.Where(x => !string.IsNullOrEmpty(x.Value)).Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value!)));
    private static JsonElement Property(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;
    private static string String(JsonElement item, string name) { var value = Property(item, name); return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : ""; }
    private static int? Integer(JsonElement item, string name) { var value = Property(item, name); return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null; }
    private static long? Long(JsonElement item, string name) { var value = Property(item, name); return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null; }
    private static bool Boolean(JsonElement item, string name) => Property(item, name).ValueKind == JsonValueKind.True;
    private static IEnumerable<JsonElement> Array(JsonElement item, string name) { var value = Property(item, name); return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : []; }
    private static string Artist(JsonElement item)
    {
        var names = Array(item, "Artists").Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (names.Count > 0) return string.Join(" / ", names);
        if (String(item, "AlbumArtist") is { Length: > 0 } albumArtist) return albumArtist;
        names = Array(item, "AlbumArtists").Select(x => String(x, "Name")).Where(x => x.Length > 0).ToList()!;
        return names.Count > 0 ? string.Join(" / ", names) : "未知艺术家";
    }

    public void Dispose() => _http.Dispose();
}
