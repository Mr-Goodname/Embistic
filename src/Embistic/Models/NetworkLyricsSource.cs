namespace Embistic.Models;

/// <summary>A GET interface returning lyric text, or a search interface followed by a lyric GET.</summary>
public sealed class NetworkLyricsSource
{
    public string Name { get; set; } = "自定义来源";
    public bool Enabled { get; set; } = true;
    public string UrlTemplate { get; set; } = "";
    public string LyricsPath { get; set; } = "";
    public string SearchUrlTemplate { get; set; } = "";
    public string SearchResultsPath { get; set; } = "";
    public string IdPath { get; set; } = "id";
    public string TitlePath { get; set; } = "name";
    public string ArtistPath { get; set; } = "artist";
    public string DurationPath { get; set; } = "";
    public bool DurationIsMilliseconds { get; set; }
    public NetworkLyricsSource Clone() => (NetworkLyricsSource)MemberwiseClone();
    public void Normalize()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "自定义来源" : Name.Trim()[..Math.Min(Name.Trim().Length, 80)];
        UrlTemplate = (UrlTemplate ?? "").Trim();
        LyricsPath = (LyricsPath ?? "").Trim();
        SearchUrlTemplate = (SearchUrlTemplate ?? "").Trim();
        SearchResultsPath = (SearchResultsPath ?? "").Trim();
        IdPath = (IdPath ?? "id").Trim();
        TitlePath = (TitlePath ?? "name").Trim();
        ArtistPath = (ArtistPath ?? "artist").Trim();
        DurationPath = (DurationPath ?? "").Trim();
    }
}
