using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Embistic.Models;
using Embistic.Services;

namespace Embistic.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _service = new();
    private CancellationTokenSource? _checkCts;
    private UpdateInfo? _info;
    private bool _closed;
    public UpdateWindow(UpdateInfo? initialInfo = null)
    {
        InitializeComponent();
        CurrentVersionLabel.Text = "当前版本 " + UpdateService.CurrentVersion;
        if (initialInfo is not null) ShowInfo(initialInfo);
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (Application.Current?.Resources["Bg"] is SolidColorBrush brush)
        {
            var dark = brush.Color.R + brush.Color.G + brush.Color.B < 384 ? 1 : 0;
            var handle = new WindowInteropHelper(this).Handle;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            var caption = brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16;
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        }
    }
    private async void Window_Loaded(object sender, RoutedEventArgs e) { if (_info is null) await CheckAsync(); }
    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();
    private async Task CheckAsync()
    {
        _checkCts?.Cancel(); _checkCts?.Dispose(); _checkCts = new CancellationTokenSource();
        var cancellationToken = _checkCts.Token;
        CheckButton.IsEnabled = false;
        StatusLabel.Text = "正在检查更新…"; DetailLabel.Text = "连接官方 GitHub 发布页。";
        try
        {
            var info = await _service.CheckAsync(cancellationToken);
            if (_closed || cancellationToken.IsCancellationRequested) return;
            if (info is null)
            {
                _info = null; StatusLabel.Text = "尚未发布更新";
                DetailLabel.Text = "当前版本可以正常使用，稍后可再次检查。";
                ReleaseNotesLabel.Text = "官方仓库目前没有公开的正式发布版本。";
                InstallerButton.Visibility = ZipButton.Visibility = Visibility.Collapsed;
            }
            else ShowInfo(info);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_closed)
            {
                StatusLabel.Text = "暂时无法检查更新";
                DetailLabel.Text = ex.Message;
            }
        }
        finally { if (!_closed) CheckButton.IsEnabled = true; }
    }
    private void ShowInfo(UpdateInfo info)
    {
        _info = info;
        StatusLabel.Text = info.IsNewer ? $"发现新版本 {info.Version}" : "已是当前最新版本";
        DetailLabel.Text = info.IsNewer ? "选择安装包或便携版，浏览器会打开官方下载链接。" : $"官方最新版本 {info.Version}";
        ReleaseNotesLabel.Text = string.IsNullOrWhiteSpace(info.ReleaseNotes) ? "此版本没有单独的发布说明。" : info.ReleaseNotes;
        InstallerButton.Visibility = info.InstallerUrl is null ? Visibility.Collapsed : Visibility.Visible;
        ZipButton.Visibility = info.ZipUrl is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ReleasePage_Click(object sender, RoutedEventArgs e) => OpenOfficial(_info?.PageUrl ?? UpdateService.RepositoryUrl + "/releases", false);
    private void Installer_Click(object sender, RoutedEventArgs e) { if (_info?.InstallerUrl is { } url) OpenOfficial(url, true); }
    private void Zip_Click(object sender, RoutedEventArgs e) { if (_info?.ZipUrl is { } url) OpenOfficial(url, true); }
    private void OpenOfficial(string url, bool download)
    {
        if (!(download ? UpdateService.IsOfficialDownloadUrl(url) : UpdateService.IsOfficialPageUrl(url))) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { DetailLabel.Text = "无法打开浏览器，请从官方发布页手动下载。"; }
    }
    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true; _checkCts?.Cancel(); _checkCts?.Dispose(); _service.Dispose();
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
