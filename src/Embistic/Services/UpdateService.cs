using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Reads public GitHub releases. It never needs a repository token or executes downloaded files.</summary>
public sealed class UpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/Mr-Goodname/Embistic";
    public const string LatestReleaseApi = "https://api.github.com/repos/Mr-Goodname/Embistic/releases/latest";
    private readonly HttpClient _http;
    private readonly string _currentVersion;
    private readonly bool _ownsClient;

    public static string CurrentVersion
    {
        get
        {
            var assembly = typeof(UpdateService).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return SemanticVersion.TryParse(version, out var parsed) ? parsed.Display : assembly.GetName().Version?.ToString(3) ?? "0.4.1";
        }
    }

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _ownsClient = true;
        _currentVersion = CurrentVersion;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Embistic/" + _currentVersion);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>Injectable transport for deterministic protocol checks.</summary>
    public UpdateService(HttpClient client, string? currentVersion = null)
    {
        _http = client ?? throw new ArgumentNullException(nameof(client));
        _currentVersion = currentVersion ?? CurrentVersion;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("暂时无法检查更新，请稍后再试。");
            if (response.Content.Headers.ContentLength is > 2_000_000)
                throw new InvalidOperationException("更新信息格式不正确。");
            var json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (json.Length > 2_000_000) throw new InvalidOperationException("更新信息格式不正确。");
            using var document = JsonDocument.Parse(json);
            var release = document.RootElement;
            var tag = String(release, "tag_name");
            if (!SemanticVersion.TryParse(tag, out var latest)) throw new InvalidOperationException("更新版本号格式不正确。");
            var page = String(release, "html_url");
            if (!IsOfficialPageUrl(page)) throw new InvalidOperationException("更新链接不属于 Embistic 官方仓库。");
            string? installer = null, zip = null;
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = String(asset, "name");
                    var url = String(asset, "browser_download_url");
                    if (!IsOfficialDownloadUrl(url) || !name.StartsWith("Embistic", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-installer.exe", StringComparison.OrdinalIgnoreCase))
                        installer ??= url;
                    else if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                        zip ??= url;
                }
            }
            var newer = SemanticVersion.TryParse(_currentVersion, out var current) && latest.CompareTo(current) > 0;
            return new UpdateInfo(latest.Display, String(release, "body"), page, installer, zip, newer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("检查更新超时，请稍后再试。"); }
        catch (JsonException) { throw new InvalidOperationException("更新信息格式不正确。"); }
        catch (HttpRequestException ex) when (ex.Message != "暂时无法检查更新，请稍后再试。" && ex.Message != "检查更新超时，请稍后再试。")
        { throw new HttpRequestException("暂时无法检查更新，请检查网络后重试。"); }
    }

    public static bool IsOfficialPageUrl(string? value) => IsOfficialUrl(value, "/Mr-Goodname/Embistic/releases", download: false);
    public static bool IsOfficialDownloadUrl(string? value) => IsOfficialUrl(value, "/Mr-Goodname/Embistic/releases/download/", download: true);
    private static bool IsOfficialUrl(string? value, string prefix, bool download)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var path = uri.AbsolutePath;
        return download ? path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && path.Length > prefix.Length
            : path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }
    private static string String(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    public void Dispose() { if (_ownsClient) _http.Dispose(); }

    private readonly record struct SemanticVersion(int Major, int Minor, int Patch, string[] Prerelease)
    {
        public string Display => $"{Major}.{Minor}.{Patch}" + (Prerelease.Length == 0 ? "" : "-" + string.Join('.', Prerelease));
        public static bool TryParse(string? value, out SemanticVersion version)
        {
            version = default;
            var match = Regex.Match(value?.Trim() ?? "", @"^[vV]?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var minor)
                || !int.TryParse(match.Groups[3].Value, out var patch)) return false;
            var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
            if (prerelease.Any(x => x.Length == 0 || (x.All(char.IsDigit) && x.Length > 1 && x[0] == '0'))) return false;
            version = new SemanticVersion(major, minor, patch, prerelease); return true;
        }
        public int CompareTo(SemanticVersion other)
        {
            var result = Major.CompareTo(other.Major); if (result != 0) return result;
            result = Minor.CompareTo(other.Minor); if (result != 0) return result;
            result = Patch.CompareTo(other.Patch); if (result != 0) return result;
            if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
                return Prerelease.Length == other.Prerelease.Length ? 0 : Prerelease.Length == 0 ? 1 : -1;
            for (var i = 0; i < Math.Min(Prerelease.Length, other.Prerelease.Length); i++)
            {
                var a = Prerelease[i]; var b = other.Prerelease[i];
                var aNumber = a.All(char.IsDigit); var bNumber = b.All(char.IsDigit);
                result = aNumber && bNumber ? a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b)
                    : aNumber != bNumber ? aNumber ? -1 : 1 : string.CompareOrdinal(a, b);
                if (result != 0) return result;
            }
            return Prerelease.Length.CompareTo(other.Prerelease.Length);
        }
    }
}
