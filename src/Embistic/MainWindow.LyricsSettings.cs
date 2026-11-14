using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Embistic.Controls;

namespace Embistic;

public partial class MainWindow
{
    private LyricAppearanceEditor? _lyricEditor;
    private LyricsSourcesEditor? _sourcesEditor;
    private SpectrumSettingsEditor? _spectrumEditor;
    private int _inlineEditorGeneration;
    private readonly DispatcherTimer _inlineSaveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };

    private void InitializeInlineSettings()
    {
        _inlineSaveTimer.Tick += (_, _) => { _inlineSaveTimer.Stop(); SaveSettings(); };
        ImmersivePlayer.SettingsPanelClosed += CloseInlineSettings;
    }

    private LyricAppearanceEditor CreateLyricsEditor()
    {
        var generation = BeginInlineEditor();
        _lyricEditor = new LyricAppearanceEditor(_settings.Appearance);
        _sourcesEditor = new LyricsSourcesEditor(_settings.LyricsSources, () => Current);
        _lyricEditor.SetSourcesEditor(_sourcesEditor);
        _lyricEditor.SetPlaybackOptions(_settings.Display.CoverPresentation, _settings.LyricsOffsetMs, _settings.DesktopLyricsEnabled);
        _lyricEditor.AppearanceChanged += appearance =>
        {
            if (generation != _inlineEditorGeneration) return;
            _settings.Appearance = appearance.Clone();
            _settings.LyricFontSize = appearance.DesktopFontSize;
            ApplyAppearance(appearance);
            ScheduleInlineSave();
        };
        _lyricEditor.CoverModeChanged += mode =>
        {
            if (generation != _inlineEditorGeneration) return;
            _settings.Display.CoverPresentation = mode;
            ImmersivePlayer.CoverPresentation = mode;
            ScheduleInlineSave();
        };
        _lyricEditor.OffsetChanged += delta => { if (generation != _inlineEditorGeneration) return; SetOffset(_settings.LyricsOffsetMs + delta); UpdateInlinePlaybackOptions(); };
        _lyricEditor.OffsetResetRequested += () => { if (generation != _inlineEditorGeneration) return; SetOffset(0); UpdateInlinePlaybackOptions(); };
        _lyricEditor.DesktopChanged += enabled => { if (generation == _inlineEditorGeneration) SetDesktopLyrics(enabled); };
        _lyricEditor.ImportRequested += () => { if (generation == _inlineEditorGeneration) ImportLyrics_Click(this, new RoutedEventArgs()); };
        _sourcesEditor.SourcesChanged += sources =>
        {
            if (generation != _inlineEditorGeneration) return;
            _settings.LyricsSources = sources;
            _lyrics.ConfigureSources(sources);
            ScheduleInlineSave();
            if (Current is { } track) _ = LoadLyricsAsync(track, _playVersion);
        };
        return _lyricEditor;
    }

    private void UpdateInlinePlaybackOptions() => _lyricEditor?.SetPlaybackOptions(_settings.Display.CoverPresentation, _settings.LyricsOffsetMs, _settings.DesktopLyricsEnabled);
    private void ShowLyricsSettingsPage(bool sources = false)
    {
        var editor = CreateLyricsEditor();
        LyricsSettingsHost.Content = editor;
        if (sources) editor.SelectSources();
    }
    private void OpenLyricsSettings(bool sources = false)
    {
        if (_section == "lyrics")
        {
            var editor = CreateLyricsEditor();
            ImmersivePlayer.ShowSettingsPanel(editor, "歌词设置");
            if (sources) editor.SelectSources();
        }
        else
        {
            if (_section != "lyricSettings") ShowPage("lyricSettings");
            else ShowLyricsSettingsPage();
            if (sources) _lyricEditor?.SelectSources();
        }
    }
    private void OpenSpectrumSettings()
    {
        // Navigation may construct the normal lyrics editor. Finish that first
        // so its callbacks cannot replace the newly opened spectrum session.
        if (_section != "lyrics" && _section != "lyricSettings") ShowPage("lyricSettings");
        var generation = BeginInlineEditor();
        _spectrumEditor = new SpectrumSettingsEditor(_settings.Spectrum);
        _spectrumEditor.PreviewChanged += settings =>
        {
            if (generation != _inlineEditorGeneration) return;
            _settings.Spectrum = settings.Clone(); ApplySpectrumSettings(settings); ScheduleInlineSave();
        };
        if (_section == "lyrics") ImmersivePlayer.ShowSettingsPanel(_spectrumEditor, "频谱设置");
        else LyricsSettingsHost.Content = _spectrumEditor;
    }
    private void ScheduleInlineSave() { _inlineSaveTimer.Stop(); _inlineSaveTimer.Start(); }
    private void FlushInlineSettings()
    {
        _lyricEditor?.Flush(); _sourcesEditor?.Flush(); _spectrumEditor?.Flush();
        if (_inlineSaveTimer.IsEnabled) { _inlineSaveTimer.Stop(); SaveSettings(); }
    }
    private int BeginInlineEditor()
    {
        FlushInlineSettings();
        _lyricEditor = null; _sourcesEditor = null; _spectrumEditor = null;
        return ++_inlineEditorGeneration;
    }
    private void CloseInlineSettings() => BeginInlineEditor();
}
