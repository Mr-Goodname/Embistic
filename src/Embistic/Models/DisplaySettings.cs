namespace Embistic.Models;

public sealed class DisplaySettings
{
    public List<string> LibraryCategories { get; set; } = new() { "albums", "albumArtists", "genres", "tracks", "playlists", "folders", "radio", "composers", "artists", "compilations", "years", "labels" };
    public List<string> HomeSections { get; set; } = new() { "favorites", "recentAlbums", "recentTracks", "frequentTracks", "playedAlbums", "frequentAlbums" };
    public bool CompactTrackRows { get; set; }
    public bool ShowSidebarPlayer { get; set; } = true;
    public bool StartInLyrics { get; set; }
    public bool RememberVolume { get; set; } = true;
    public int StartupVolume { get; set; } = 45;
    public bool DiscAnimation { get; set; } = true;
    public List<RadioStation> RadioStations { get; set; } = new();
    public DisplaySettings Clone()
    {
        var copy = new DisplaySettings
        {
            CompactTrackRows = CompactTrackRows, ShowSidebarPlayer = ShowSidebarPlayer,
            StartInLyrics = StartInLyrics, RememberVolume = RememberVolume,
            StartupVolume = StartupVolume, DiscAnimation = DiscAnimation
        };
        if (LibraryCategories is not null) copy.LibraryCategories = new(LibraryCategories);
        if (HomeSections is not null) copy.HomeSections = new(HomeSections);
        if (RadioStations is not null)
            copy.RadioStations = RadioStations.Where(s => s is not null).Select(s => new RadioStation { Name = s.Name, Url = s.Url }).ToList();
        copy.Normalize();
        return copy;
    }
    public void Normalize()
    {
        var defaults = new DisplaySettings();
        LibraryCategories = (LibraryCategories ?? defaults.LibraryCategories).Where(defaults.LibraryCategories.Contains).Distinct().ToList();
        HomeSections = (HomeSections ?? defaults.HomeSections).Where(defaults.HomeSections.Contains).Distinct().ToList();
        StartupVolume = Math.Clamp(StartupVolume, 0, 100);
        RadioStations ??= new();
        RadioStations = RadioStations.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Name)
            && s.Url is { Length: <= 4096 } && Uri.TryCreate(s.Url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)).ToList();
    }
}

public sealed class RadioStation { public string Name { get; set; } = ""; public string Url { get; set; } = ""; }
public sealed record LibraryShortcut(string Id, string Name, string Glyph);
