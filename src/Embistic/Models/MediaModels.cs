namespace Embistic.Models;

public sealed class Track
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string AlbumId { get; set; } = "";
    public long DurationMs { get; set; }
    public string Container { get; set; } = "";
    public int? BitRate { get; set; }
    public int? SampleRate { get; set; }
    public int? BitDepth { get; set; }
    public int Index { get; set; }
    public bool IsFavorite { get; set; }
    public string ImageUrl { get; set; } = "";
    public int PlayCount { get; set; }
    public DateTimeOffset? LastPlayedDate { get; set; }
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = new();
    public List<string> Composers { get; set; } = new();
    public List<string> Labels { get; set; } = new();
    public bool IsCompilation { get; set; }
    public string Path { get; set; } = "";
    public string DurationText => TimeSpan.FromMilliseconds(DurationMs).ToString(DurationMs >= 3600000 ? @"h\:mm\:ss" : @"m\:ss");
    public string QualityText => string.Join(" · ", new[] { Container.ToUpperInvariant(), BitDepth is > 0 ? $"{BitDepth} bit" : null, SampleRate is > 0 ? $"{SampleRate / 1000.0:0.#} kHz" : null }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed class Album
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public int? Year { get; set; }
    public bool IsFavorite { get; set; }
}

public sealed record EmbyUser(string Id, string Name);
public sealed record AudioDevice(string Id, string Name);
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public string AudioDeviceId { get; set; } = "";
    public int Volume { get; set; } = 65;
    public bool DesktopLyricsEnabled { get; set; }
    public double LyricsOffsetMs { get; set; }
    public double LyricFontSize { get; set; } = 32;
    public double LyricsLeft { get; set; } = 300;
    public double LyricsTop { get; set; } = 720;
    public bool LyricsLocked { get; set; }
    public LyricAppearanceSettings Appearance { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();
    public SpectrumSettings Spectrum { get; set; } = new();
    public string LoginMode { get; set; } = "ApiKey";
    public bool CheckUpdatesAutomatically { get; set; } = true;
    public DisplaySettings Display { get; set; } = new();
    public bool CloseToTray { get; set; } = true;
    public bool AutoCacheMusic { get; set; }
    public string DownloadDirectory { get; set; } = "";
    public List<NetworkLyricsSource> LyricsSources { get; set; } = new();
}
