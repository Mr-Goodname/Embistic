namespace Embistic.Models;

public sealed record LyricsLine(TimeSpan Time, string Text);

public sealed class LyricsDocument
{
    public IReadOnlyList<LyricsLine> Lines { get; init; } = Array.Empty<LyricsLine>();
    public string PlainText { get; init; } = "";
    public string Source { get; init; } = "";
    public bool IsSynced { get; init; }
    public bool IsInstrumental { get; init; }

    public static LyricsDocument Empty(string message = "暂未找到歌词") => new() { PlainText = message };
}
