using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;
using Microsoft.Win32;

namespace Embistic;

public partial class MainWindow
{
    private void LoginMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PasswordLoginPanel is null || ApiLoginPanel is null) return;
        var password = LoginModeBox.SelectedValue as string == "Password";
        PasswordLoginPanel.Visibility = password ? Visibility.Visible : Visibility.Collapsed;
        ApiLoginPanel.Visibility = password ? Visibility.Collapsed : Visibility.Visible;
    }
    private async void PasswordLogin_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        PasswordLoginButton.IsEnabled = false;
        Status = "正在登录 Emby…";
        try
        {
            var server = ServerBox.Text.Trim();
            var result = await EmbyAuthenticationService.AuthenticateAsync(server, UsernameBox.Text, LoginPasswordBox.Password);
            if (_closing) return;
            await StopForReconnectAsync();
            _settings.ServerUrl = server; _settings.ApiKey = result.AccessToken; _settings.UserId = result.UserId; _settings.UserName = result.UserName; _settings.LoginMode = "Password";
            LoginPasswordBox.Clear();
            ApiKeyBox.Password = result.AccessToken;
            InitializeLocalStores();
            await ConnectAsync();
        }
        finally { PasswordLoginButton.IsEnabled = true; }
    });
    private async Task StopForReconnectAsync()
    {
        ++_playVersion; ++_connectVersion;
        _queueSelectionCts?.Cancel(); _pageCts?.Cancel(); _lyricCts?.Cancel();
        _document = LyricsDocument.Empty(); UpdateLyricsBindings();
        await _transportGate.WaitAsync();
        try
        {
            if (Current is { } track) { _history?.EndTrack(track, _position); await ReportAsync(track, _position, true, "stop"); }
            if (_renderer is { } device) { try { await _dlna.StopAsync(device).WaitAsync(TimeSpan.FromSeconds(2)); } catch { } _renderer = null; }
            _player?.Stop(); _playing = false; _position = 0; _duration = 0; Current = null; Queue.Clear(); _queueIndex = -1;
            ImmersivePlayer.SetTrack(null); SyncSpectrumCapture(); NotifyNowPlaying();
            _emby?.Dispose(); _emby = null;
            _history = null;
            _downloads?.Dispose(); _downloads = null;
        }
        finally { _transportGate.Release(); }
    }
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var original = _settings.Theme.Clone();
        var editor = new ThemeSettingsWindow(original) { Owner = this };
        editor.PreviewChanged += settings => _theme.Apply(settings);
        if (editor.ShowDialog() == true) { _settings.Theme = editor.Result.Clone(); _theme.Apply(_settings.Theme); SaveSettings(); Status = "主题已保存。"; }
        else _theme.Apply(original);
    }
    private void DisplaySettings_Click(object sender, RoutedEventArgs e)
    {
        var editor = new DisplaySettingsWindow(_settings.Display) { Owner = this };
        editor.SourceInitialized += (_,_) => _theme.ApplyWindowChrome(editor);
        if (editor.ShowDialog() == true)
        {
            _settings.Display = editor.Result.Clone(); ApplyDisplaySettings(); SaveSettings(); Status = "界面与播放设置已保存。";
        }
    }
    private void ApplySpectrumSettings(SpectrumSettings settings)
    {
        _spectrumSettings = settings.Clone(); _spectrumSettings.Normalize();
        ImmersivePlayer.ApplySpectrum(_spectrumSettings);
        _spectrumAttempted = false;
        if (_ready) SyncSpectrumCapture();
    }
    private void SpectrumSettings_Click(object sender, RoutedEventArgs e)
    {
        var original = _settings.Spectrum.Clone();
        var editor = new SpectrumSettingsWindow(original) { Owner = this };
        editor.SourceInitialized += (_,_) => _theme.ApplyWindowChrome(editor);
        editor.PreviewChanged += ApplySpectrumSettings;
        if (editor.ShowDialog() == true) { _settings.Spectrum = editor.Result.Clone(); ApplySpectrumSettings(_settings.Spectrum); SaveSettings(); Status = "频谱设置已保存。"; }
        else ApplySpectrumSettings(original);
    }
    private void SyncSpectrumCapture()
    {
        if (_closing) return;
        var wanted = _ready && _section == "lyrics" && _playing && Current is not null && _renderer is null && _spectrumSettings.Enabled;
        ImmersivePlayer.Spectrum.IsPlaying = wanted;
        if (!wanted)
        {
            if (_spectrumService.IsRunning) _spectrumService.Stop();
            _spectrumAttempted = false;
            ImmersivePlayer.SetSpectrumStatus(_renderer is not null && _spectrumSettings.Enabled ? "DLNA 在音箱播放，本机无法读取远端音频频谱。" : "");
            return;
        }
        if (_spectrumService.IsRunning && _spectrumService.EndpointId != _settings.AudioDeviceId) { _spectrumService.Stop(); _spectrumAttempted = false; }
        if (!_spectrumService.IsRunning && !_spectrumAttempted)
        {
            _spectrumAttempted = true;
            _spectrumService.Start(_settings.AudioDeviceId);
        }
    }
    private void AutoUpdate_Click(object sender, RoutedEventArgs e) { _settings.CheckUpdatesAutomatically = AutoUpdateBox.IsChecked == true; SaveSettings(); }
    private void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var editor = new UpdateWindow { Owner = this }; editor.SourceInitialized += (_,_) => _theme.ApplyWindowChrome(editor); editor.ShowDialog();
    }
    private async Task CheckUpdatesQuietlyAsync()
    {
        try
        {
            using var service = new UpdateService();
            var update = await service.CheckAsync();
            if (!_closing && update?.IsNewer == true) Status = $"发现新版本 {update.Version}，可在设置中查看更新。";
        }
        catch { }
    }
    private void Github_Click(object sender, RoutedEventArgs e) => OpenUrl(UpdateService.RepositoryUrl);
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        _downloads ??= new DownloadService(_settings);
        var tracks = _downloads.GetTracks().ToList();
        if (_emby is not null) foreach (var track in tracks) track.ImageUrl = _emby.GetImageUrl(track.AlbumId.Length > 0 ? track.AlbumId : track.Id);
        ShowCollection(tracks, "已下载音乐", "已保存到此电脑，可直接播放。");
    }
    private void DownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        _downloads ??= new DownloadService(_settings); Directory.CreateDirectory(_downloads.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _downloads.DirectoryPath }, UseShellExecute = true });
    }
    private async Task DownloadCurrentAsync()
    {
        if (Current is not { } track || _emby is null || track.Id.StartsWith("radio:")) { Status = "选择一首 Emby 歌曲后再下载。"; return; }
        _downloads ??= new DownloadService(_settings);
        Status = "正在下载 · " + track.Title;
        await _downloads.DownloadAsync(track, _emby.GetStreamUrl(track), new Progress<double>(value => Status = $"下载 {value:0}% · {track.Title}"));
        Status = "已下载 · " + track.Title;
    }
    private void BackupSettings_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Embistic 设置|*.json", FileName = "Embistic-settings.json" };
        if (picker.ShowDialog(this) == true)
        {
            var preset = new AppearanceBackup { Appearance = _settings.Appearance, Theme = _settings.Theme, Spectrum = _settings.Spectrum, Display = _settings.Display, LyricsOffsetMs = _settings.LyricsOffsetMs };
            File.WriteAllText(picker.FileName, JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true }));
            Status = "界面设置已备份，不包含登录凭据。";
        }
    }
    private void RestoreSettings_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Embistic 设置|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var preset = JsonSerializer.Deserialize<AppearanceBackup>(File.ReadAllText(picker.FileName)) ?? throw new JsonException();
            preset.Appearance.Normalize(); preset.Theme.Normalize(); preset.Spectrum.Normalize(); preset.Display.Normalize();
            _settings.Appearance = preset.Appearance; _settings.Theme = preset.Theme; _settings.Spectrum = preset.Spectrum; _settings.Display = preset.Display;
            SetOffset(preset.LyricsOffsetMs); ApplyAppearance(_settings.Appearance); _theme.Apply(_settings.Theme); ApplySpectrumSettings(_settings.Spectrum); ApplyDisplaySettings(); SaveSettings();
            Status = "界面设置已恢复。";
        }
        catch (Exception ex) when (ex is IOException or JsonException or NullReferenceException) { Status = "无法读取这份设置文件。"; }
    }
    public sealed class AppearanceBackup
    {
        public LyricAppearanceSettings Appearance { get; set; } = new();
        public ThemeSettings Theme { get; set; } = new();
        public SpectrumSettings Spectrum { get; set; } = new();
        public DisplaySettings Display { get; set; } = new();
        public double LyricsOffsetMs { get; set; }
    }
}
