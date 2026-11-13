using System.Windows;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;

namespace Embistic;

public partial class MainWindow
{
    private TrayService? _tray;
    private bool _exitRequested;
    private CancellationTokenSource? _downloadCts;
    private Task _downloadTask = Task.CompletedTask;
    private Track? _pendingAutomaticCache;
    public string DownloadDirectoryLabel => "下载目录：" + (_settings.DownloadDirectory.Length > 0 ? _settings.DownloadDirectory : DownloadService.DefaultRoot);
    private void InitializeTray()
    {
        _tray = new TrayService(this);
        _tray.RestoreRequested += RestoreFromTray;
        _tray.TogglePlayRequested += async () => await GuardAsync(TogglePlayAsync);
        _tray.PreviousRequested += () => Previous_Click(this, new RoutedEventArgs());
        _tray.NextRequested += async () => await GuardAsync(() => AdvanceAsync(false));
        _tray.ExitRequested += () => { _exitRequested = true; Close(); };
        IsVisibleChanged += (_, _) => SyncSpectrumCapture();
        StateChanged += (_, _) => SyncSpectrumCapture();
        Application.Current.SessionEnding += (_, _) => _exitRequested = true;
        _tray.Update(CurrentTitle, _playing);
    }
    private void RestoreFromTray()
    {
        if (_closing) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }
    private async void BackgroundSettings_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BackgroundSettingsWindow(_settings, DownloadService.DefaultRoot) { Owner = this };
        editor.SourceInitialized += (_, _) => _theme.ApplyWindowChrome(editor);
        if (editor.ShowDialog() != true) return;
        await GuardAsync(async () =>
        {
            _pendingAutomaticCache = null;
            _downloadCts?.Cancel();
            await _downloadTask;
            _downloads ??= new DownloadService(_settings);
            Status = "正在更新音乐下载目录…";
            await _downloads.ChangeDirectoryAsync(editor.DownloadDirectory);
            _settings.DownloadDirectory = editor.DownloadDirectory;
            _settings.CloseToTray = editor.CloseToTray;
            _settings.AutoCacheMusic = editor.AutoCacheMusic;
            SaveSettings(); Notify(nameof(DownloadDirectoryLabel));
            Status = "后台播放与缓存设置已保存。";
            if (_settings.AutoCacheMusic && Current is { } track) StartAutomaticCache(track);
        });
    }
    private void StartAutomaticCache(Track track)
    {
        if (!_settings.AutoCacheMusic || _emby is null || PlaybackSourceResolver.IsRadio(track) || _downloads?.IsDownloaded(track) == true) return;
        if (!_downloadTask.IsCompleted) { _pendingAutomaticCache = track; return; }
        _downloadTask = DownloadTrackAsync(track, automatic: true);
    }
    private async Task DownloadTrackAsync(Track track, bool automatic = false)
    {
        if (_emby is not { } client || PlaybackSourceResolver.IsRadio(track)) { Status = "选择一首 Emby 歌曲后再下载。"; return; }
        if (_downloadCts is not null) { Status = "已有下载进行中，可在更多菜单取消。"; return; }
        var cancellation = new CancellationTokenSource();
        _downloadCts = cancellation;
        var store = _downloads ??= new DownloadService(_settings);
        try
        {
            if (!automatic) Status = "正在下载 · " + track.Title;
            IProgress<double>? progress = automatic ? null : new Progress<double>(value => { if (!_closing && client == _emby) Status = $"下载 {value:0}% · {track.Title}"; });
            await store.DownloadAsync(track, client.GetStreamUrl(track), progress, cancellation.Token);
            if (!_closing && client == _emby) Status = (automatic ? "已缓存 · " : "已下载 · ") + track.Title;
        }
        catch (OperationCanceledException) { if (!_closing && !automatic) Status = "下载已取消。"; }
        catch (Exception ex) { if (!_closing) Status = (automatic ? "自动缓存未完成：" : "下载未完成：") + SafeMessage(ex); }
        finally
        {
            if (_downloadCts == cancellation) _downloadCts = null;
            cancellation.Dispose();
            if (_pendingAutomaticCache is { } pending)
            {
                _pendingAutomaticCache = null;
                _ = Dispatcher.BeginInvoke(() => { if (!_closing && Current?.Id == pending.Id) StartAutomaticCache(pending); });
            }
        }
    }
    private void CancelDownload() { _downloadCts?.Cancel(); Status = "正在取消下载…"; }
}
