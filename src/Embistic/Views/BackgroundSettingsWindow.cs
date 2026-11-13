using System.IO;
using System.Windows;
using System.Windows.Controls;
using Embistic.Models;
using Microsoft.Win32;

namespace Embistic.Views;

public sealed class BackgroundSettingsWindow : Window
{
    private readonly CheckBox _tray, _cache;
    private readonly TextBox _directory;
    public bool CloseToTray => _tray.IsChecked == true;
    public bool AutoCacheMusic => _cache.IsChecked == true;
    public string DownloadDirectory => _directory.Text.Trim();
    public BackgroundSettingsWindow(AppSettings settings, string defaultDirectory)
    {
        Title = "后台播放与音乐缓存"; Width = 640; Height = 450; MinWidth = 520; MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(28) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0,0,10,0) };
        var save = new Button { Content = "保存", IsDefault = true };
        save.Click += (_, _) =>
        {
            try
            {
                if (!Path.IsPathFullyQualified(DownloadDirectory)) throw new ArgumentException();
                Directory.CreateDirectory(DownloadDirectory);
                var probe = Path.Combine(DownloadDirectory, ".embistic-write-" + Guid.NewGuid().ToString("N"));
                using (File.Create(probe)) { }
                File.Delete(probe);
                DialogResult = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { MessageBox.Show(this, "请选择可以写入的完整下载目录。", "下载目录", MessageBoxButton.OK, MessageBoxImage.Information); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); root.Children.Add(buttons);
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "后台播放与音乐缓存", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,16) });
        _tray = new CheckBox { Content = "关闭主窗口时隐藏到托盘，继续播放音乐", IsChecked = settings.CloseToTray };
        _cache = new CheckBox { Content = "播放 Emby 歌曲时自动缓存完整音频", IsChecked = settings.AutoCacheMusic };
        panel.Children.Add(_tray); panel.Children.Add(_cache);
        panel.Children.Add(new TextBlock { Text = "托盘右键可控制播放、切歌和退出。已缓存歌曲在本机优先播放，断开服务器后也可聆听。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,20) });
        panel.Children.Add(new TextBlock { Text = "音乐下载目录", Margin = new Thickness(0,0,0,8) });
        var pathRow = new DockPanel();
        var choose = new Button { Content = "选择…", Margin = new Thickness(10,0,0,0) }; DockPanel.SetDock(choose, Dock.Right);
        _directory = new TextBox { Text = settings.DownloadDirectory.Length > 0 ? settings.DownloadDirectory : defaultDirectory };
        choose.Click += (_, _) => { var picker = new OpenFolderDialog { Title = "选择音乐缓存与下载目录", InitialDirectory = Directory.Exists(_directory.Text) ? _directory.Text : defaultDirectory }; if (picker.ShowDialog(this) == true) _directory.Text = picker.FolderName; };
        pathRow.Children.Add(choose); pathRow.Children.Add(_directory); panel.Children.Add(pathRow);
        panel.Children.Add(new TextBlock { Text = "目录内按服务器和账号分开保存。切换目录会复制当前账号已有的下载，原文件保留。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0), FontSize = 12 });
        root.Children.Add(panel); Content = root;
    }
}
