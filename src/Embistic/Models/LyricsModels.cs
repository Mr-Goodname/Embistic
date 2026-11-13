namespace Embistic.Models;

/// <summary>Absolute song times and the corresponding text range, as provided by the lyric file.</summary>
public sealed record LyricsWord(TimeSpan Start, TimeSpan End, string Text, int TextIndex = 0);

public sealed record LyricsLine(TimeSpan Time, string Text)
{
    public IReadOnlyList<LyricsWord> Words { get; init; } = Array.Empty<LyricsWord>();
    public TimeSpan? EndTime { get; init; }
}

public sealed class LyricsDocument
{
    public IReadOnlyList<LyricsLine> Lines { get; init; } = Array.Empty<LyricsLine>();
    public string PlainText { get; init; } = "";
    public string Source { get; init; } = "";
    public bool IsSynced { get; init; }
    public bool IsInstrumental { get; init; }

    public static LyricsDocument Empty(string message = "暂未找到歌词") => new() { PlainText = message };
}
