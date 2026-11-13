using System.Windows;
using Embistic.Views;

namespace Embistic;

public partial class MainWindow
{
    private void LyricsSources_Click(object sender, RoutedEventArgs e)
    {
        var editor = new LyricsSourcesWindow(_settings.LyricsSources, Current) { Owner = this };
        editor.SourceInitialized += (_, _) => _theme.ApplyWindowChrome(editor);
        if (editor.ShowDialog() != true) return;
        _settings.LyricsSources = editor.Result;
        _lyrics.ConfigureSources(_settings.LyricsSources);
        SaveSettings(); Status = "网络歌词来源已保存。";
        if (Current is { } track) _ = LoadLyricsAsync(track, _playVersion);
    }
}
