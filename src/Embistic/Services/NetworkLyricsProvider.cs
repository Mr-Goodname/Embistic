using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Embistic.Models;

namespace Embistic.Services;

public static class NetworkLyricsProvider
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private const int MaxBytes = 2_000_000;
    public static string BuildUrl(string template, Track track, string? id = null)
    {
        var values = new Dictionary<string, string>
        {
            ["title"] = track.Title, ["artist"] = track.Artist, ["album"] = track.Album,
            ["duration"] = (track.DurationMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture),
            ["durationMs"] = track.DurationMs.ToString(CultureInfo.InvariantCulture), ["id"] = id ?? track.Id
        };
        var url = Regex.Replace(template, @"\{([A-Za-z]+)\}", match => values.TryGetValue(match.Groups[1].Value, out var value)
            ? Uri.EscapeDataString(value) : throw new ArgumentException("接口模板包含不支持的占位符。"));
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new ArgumentException("歌词接口需使用有效的 HTTP 或 HTTPS 地址。");
        return uri.AbsoluteUri;
    }
    public static JsonElement ReadPath(JsonElement value, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$") return value;
        foreach (var part in path.TrimStart('$', '.').Replace("[", ".").Replace("]", "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(part, out var child)) value = child;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
            else return default;
        }
        return value;
    }
    private static string Text(JsonElement value) => value.ValueKind switch
    { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.Number => value.GetRawText(), _ => "" };
    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    public static string? SelectId(JsonElement root, NetworkLyricsSource source, Track track)
    {
        var results = ReadPath(root, source.SearchResultsPath);
        if (results.ValueKind != JsonValueKind.Array) return null;
        string? best = null;
        var bestDistance = double.MaxValue;
        foreach (var result in results.EnumerateArray().Take(100))
        {
            var title = Text(ReadPath(result, source.TitlePath));
            var artist = Text(ReadPath(result, source.ArtistPath));
            if (Normalize(title) != Normalize(track.Title) || Normalize(artist) != Normalize(track.Artist)) continue;
            var distance = 0d;
            if (!string.IsNullOrWhiteSpace(source.DurationPath))
            {
                var duration = Text(ReadPath(result, source.DurationPath));
                if (!double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0) continue;
                if (source.DurationIsMilliseconds) seconds /= 1000;
                if (track.DurationMs > 0) { distance = Math.Abs(seconds - track.DurationMs / 1000d); if (distance > 12) continue; }
            }
            var id = Text(ReadPath(result, source.IdPath));
            if (id.Length == 0 || distance >= bestDistance) continue;
            best = id; bestDistance = distance;
        }
        return best;
    }
    public static async Task<LyricsDocument> FetchAsync(NetworkLyricsSource source, Track track, CancellationToken ct = default)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            string? id = null;
            if (source.SearchUrlTemplate.Length > 0)
            {
                using var search = JsonDocument.Parse(await GetTextAsync(BuildUrl(source.SearchUrlTemplate, track), bounded.Token).ConfigureAwait(false));
                id = SelectId(search.RootElement, source, track);
                if (id is null) return LyricsDocument.Empty();
            }
            var response = await GetTextAsync(BuildUrl(source.UrlTemplate, track, id), bounded.Token).ConfigureAwait(false);
            string text;
            if (source.LyricsPath.Length > 0)
            {
                using var json = JsonDocument.Parse(response);
                text = Text(ReadPath(json.RootElement, source.LyricsPath));
            }
            else text = response.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (text.StartsWith('['))
            {
                try { using var unexpectedJson = JsonDocument.Parse(text); return LyricsDocument.Empty(); }
                catch (JsonException) { /* A leading LRC timestamp or metadata tag is valid lyric text. */ }
            }
            if (text.StartsWith('{') || text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return LyricsDocument.Empty();
            return LyricsService.ParseLrc(text, source.Name);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return LyricsDocument.Empty(); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException or System.IO.IOException) { return LyricsDocument.Empty(); }
    }
    private static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Embistic/0.4");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxBytes) throw new System.IO.IOException("歌词响应过大。");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new System.IO.MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > MaxBytes) throw new System.IO.IOException("歌词响应过大。");
            output.Write(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
