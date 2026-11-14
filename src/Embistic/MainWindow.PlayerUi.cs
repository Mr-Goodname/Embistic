using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using Embistic.Models;
using Embistic.Views;

namespace Embistic;

public partial class MainWindow
{
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private WindowState _previousWindowState;
    private bool _fullscreen;
    private readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private DateTime _titleInteractionAt, _playerInteractionAt;
    private bool _chromeInitialized, _immersiveChrome, _titleShown = true, _playerShown = true;
    private bool _chromeKeyboardInteraction;

    private void PlayerChrome_Loaded(object sender, RoutedEventArgs e)
    {
        if (_chromeInitialized) return;
        _chromeInitialized = true;
        BottomPlayerBar.RenderTransform = new TranslateTransform();
        TitleBar.RenderTransform = new TranslateTransform();
        BottomPlayerBar.SizeChanged += (_, _) =>
        {
            var slide = (TranslateTransform)BottomPlayerBar.RenderTransform;
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = _playerShown ? 0 : BottomPlayerBar.ActualHeight;
            ImmersivePlayer.SetPlayerChrome(BottomPlayerBar.ActualHeight, _playerShown, false);
        };
        PreviewMouseMove += (_, _) => UpdateChromeFromPointer();
        PreviewKeyDown += (_, _) => { _chromeKeyboardInteraction = true; ShowTitleChrome(); ShowPlayerChrome(); };
        PreviewMouseDown += (_, _) => _chromeKeyboardInteraction = false;
        OutputPopup.Child.PreviewMouseDown += (_, _) => _chromeKeyboardInteraction = false;
        OutputPopup.Child.PreviewKeyDown += (_, _) => _chromeKeyboardInteraction = true;
        StateChanged += (_, _) => UpdateCaptionState();
        ImmersivePlayer.IsVisibleChanged += (_, _) => UpdateImmersiveChrome();
        OutputPopup.Opened += (_, _) => ShowPlayerChrome();
        _chromeTimer.Tick += (_, _) => UpdateChromeFromPointer();
        Closed += (_, _) => _chromeTimer.Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateImmersiveChrome(); else _chromeTimer.Stop(); };
        UpdateImmersiveChrome(); UpdateCaptionState();
    }

    private void UpdateImmersiveChrome()
    {
        _immersiveChrome = ImmersivePlayer.Visibility == Visibility.Visible;
        CaptionRow.Height = new GridLength(_immersiveChrome || _fullscreen ? 0 : 40);
        ShowTitleChrome(); ShowPlayerChrome();
        ImmersivePlayer.SetPlayerChrome(BottomPlayerBar.ActualHeight, _playerShown, false);
        if (_immersiveChrome || _fullscreen) _chromeTimer.Start(); else _chromeTimer.Stop();
    }

    private void UpdateChromeFromPointer()
    {
        if (!_immersiveChrome && !_fullscreen) return;
        var point = Mouse.GetPosition(WindowFrame);
        var inside = point.X >= 0 && point.X <= WindowFrame.ActualWidth && point.Y >= 0 && point.Y <= WindowFrame.ActualHeight;
        var pointerAtTop = inside && point.Y <= 112;
        var pointerAtBottom = inside && point.Y >= WindowFrame.ActualHeight - Math.Max(118, BottomPlayerBar.ActualHeight + 12);
        var sharedInteraction = Mouse.LeftButton == MouseButtonState.Pressed || ImmersivePlayer.HasOpenMenu;
        if (pointerAtTop || sharedInteraction || (_chromeKeyboardInteraction && TitleBar.IsKeyboardFocusWithin) || TitleBar.IsMouseCaptureWithin) ShowTitleChrome();
        else if (DateTime.UtcNow - _titleInteractionAt > TimeSpan.FromSeconds(1.6)) SetChromeVisible(TitleBar, false, ref _titleShown);
        if (!_immersiveChrome) { ShowPlayerChrome(); return; }
        if (pointerAtBottom || sharedInteraction || OutputPopup.IsOpen || QueuePopup.IsOpen || PlayerCoverMenu.IsOpen || (_chromeKeyboardInteraction && BottomPlayerBar.IsKeyboardFocusWithin) || BottomPlayerBar.IsMouseCaptureWithin) ShowPlayerChrome();
        else if (DateTime.UtcNow - _playerInteractionAt > TimeSpan.FromSeconds(1.6)) SetChromeVisible(BottomPlayerBar, false, ref _playerShown);
    }

    private void ShowTitleChrome() { _titleInteractionAt = DateTime.UtcNow; SetChromeVisible(TitleBar, true, ref _titleShown); }
    private void ShowPlayerChrome() { _playerInteractionAt = DateTime.UtcNow; SetChromeVisible(BottomPlayerBar, true, ref _playerShown); }
    private void SetChromeVisible(FrameworkElement element, bool visible, ref bool state)
    {
        if (state == visible) return;
        state = visible;
        var previous = element.Opacity;
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = visible ? 1 : 0;
        element.IsHitTestVisible = visible;
        var animate = _appearance.AnimationsEnabled && SystemParameters.ClientAreaAnimation;
        if (animate)
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(previous, visible ? 1 : 0, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
        if (element.RenderTransform is TranslateTransform slide)
        {
            var from = slide.Y;
            var target = visible ? 0 : (ReferenceEquals(element, BottomPlayerBar) ? element.ActualHeight : -element.ActualHeight);
            slide.BeginAnimation(TranslateTransform.YProperty, null);
            slide.Y = target;
            if (animate)
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(220))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        if (ReferenceEquals(element, BottomPlayerBar))
            ImmersivePlayer.SetPlayerChrome(BottomPlayerBar.ActualHeight, visible, animate);
    }

    private void UpdateCaptionState()
    {
        TitleMaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        TitleMaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";
        WindowFrame.Margin = !_fullscreen && WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        var rounded = !_fullscreen && WindowState == WindowState.Normal;
        var chrome = WindowChrome.GetWindowChrome(this);
        // Windows 11 rounds the compositor surface; Windows 10 uses WindowChrome's
        // native region. Both paths retain hardware rendering and resize hit testing.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            var preference = rounded ? 2 : 1; // DWMWCP_ROUND / DWMWCP_DONOTROUND
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
            chrome.CornerRadius = new CornerRadius(0);
        }
        else chrome.CornerRadius = new CornerRadius(rounded ? 8 : 0);
    }
    private void TitleMinimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void TitleMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) ToggleFullscreen();
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }
    private void TitleClose_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _previousWindowStyle = WindowStyle; _previousResizeMode = ResizeMode; _previousWindowState = WindowState;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
        else { WindowState = WindowState.Normal; WindowStyle = _previousWindowStyle; ResizeMode = _previousResizeMode; WindowState = _previousWindowState; }
        _fullscreen = !_fullscreen;
        var chrome = WindowChrome.GetWindowChrome(this);
        chrome.CaptionHeight = _fullscreen ? 0 : 40;
        chrome.ResizeBorderThickness = new Thickness(_fullscreen ? 0 : 7);
        TitleFullscreenButton.Content = _fullscreen ? "\uE73F" : "\uE740";
        UpdateImmersiveChrome(); UpdateCaptionState();
    }
    private void PlayerCover_Click(object sender, RoutedEventArgs e) => ShowPage("lyrics");
    private async Task DiscoverPopupDevicesAsync()
    {
        if (!DiscoverButton.IsEnabled) return;
        DiscoverButton.IsEnabled = false; DlnaStatus = "正在搜索局域网音箱…";
        try
        {
            Replace(DlnaDevices, await _dlna.DiscoverAsync());
            DlnaStatus = DlnaDevices.Count > 0 ? $"找到 {DlnaDevices.Count} 个设备" : "未发现设备，请确认音箱已开启 DLNA。";
        }
        catch { DlnaStatus = "搜索失败，请检查局域网连接。"; }
        finally { DiscoverButton.IsEnabled = true; }
    }
    private async void PopupDiscover_Click(object sender, RoutedEventArgs e) => await DiscoverPopupDevicesAsync();
    private void PopupAudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PopupAudioDeviceBox.SelectedItem is not AudioDevice device) return;
        try { _player?.SetAudioDevice(device.Id); _settings.AudioDeviceId = device.Id; AudioDeviceBox.SelectedValue = device.Id; Notify(nameof(OutputLabel)); SaveSettings(); SyncSpectrumCapture(); }
        catch (Exception ex) { Status = SafeMessage(ex); }
    }
    private async void PopupUseLocal_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => { await SwitchOutputAsync(null); OutputPopup.IsOpen = false; SyncSpectrumCapture(); });
    private async void PopupCastDevice_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (sender is not FrameworkElement { DataContext: DlnaDevice device }) return;
        if (Current is null) { DlnaStatus = "请先选择一首歌曲。"; return; }
        await SwitchOutputAsync(device); OutputPopup.IsOpen = false; SyncSpectrumCapture();
    });
    private void PlayerCover_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Current is not { } track) { e.Handled = true; return; }
        var menu = PlayerCoverMenu; menu.Items.Clear();
        var header = new StackPanel { Margin = new Thickness(10), Width = 260 };
        header.Children.Add(new TextBlock { Text = track.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var artist = new TextBlock { Text = track.Artist, Margin = new Thickness(0,6,0,0), TextWrapping = TextWrapping.Wrap }; artist.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk"); header.Children.Add(artist);
        var album = new TextBlock { Text = track.Album, Margin = new Thickness(0,4,0,0), TextWrapping = TextWrapping.Wrap }; album.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); header.Children.Add(album);
        menu.Items.Add(new MenuItem { Header = header, IsEnabled = false });
        AddMenu(menu, _playing ? "暂停" : "播放", async () => await TogglePlayAsync());
        AddMenu(menu, "下一首播放", () => { Queue.Insert(Math.Clamp(_queueIndex+1,0,Queue.Count), track); return Task.CompletedTask; });
        AddMenu(menu, "从待播队列移除同名歌曲", () => { for(var i=Queue.Count-1;i>=0;i--)if(Queue[i].Id==track.Id)Queue.RemoveAt(i); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        AddMenu(menu, track.IsFavorite ? "取消收藏" : "添加到收藏", () => ToggleFavoriteAsync(track));
        var localLists = new MenuItem { Header = "添加到本地播放列表" }; PopulateLocalPlaylistMenu(localLists, track); menu.Items.Add(localLists);
        AddMenu(menu, "添加到 Emby 播放列表", () => AddCurrentToPlaylistAsync());
        AddMenu(menu, "专辑混播", async () => { if(_emby is null)return; var result=await _emby.GetTracksAsync(albumId:track.AlbumId,limit:1000);await StartQueueAsync(result.Items,true); });
        menu.Items.Add(new Separator());
        AddMenu(menu, "转到专辑", async () => { if(_emby is null||track.AlbumId.Length==0)return; await OpenAlbumAsync(new Album { Id=track.AlbumId, Title=track.Album, Artist=track.Artist }); });
        AddMenu(menu, "转到艺术家", async () => { if(_emby is null)return; var artists=await _emby.GetArtistsAsync();var item=artists.FirstOrDefault(a=>string.Equals(a.Name,track.Artist,StringComparison.CurrentCultureIgnoreCase));if(item is not null)ShowCollection(await _emby.GetLibraryItemTracksAsync(item),item.Name,"艺术家");else{SearchBox.Text=track.Artist;await ApplySearchAsync();} });
        AddMenu(menu, "歌词", () => { ShowPage("lyrics"); return Task.CompletedTask; });
        AddMenu(menu, "下载到此电脑", DownloadCurrentAsync);
        if (!_downloadTask.IsCompleted) AddMenu(menu, "取消下载", () => { CancelDownload(); return Task.CompletedTask; });
        AddMenu(menu, "歌曲信息", () => { MessageBox.Show(this, $"{track.Title}\n{track.Artist}\n{track.Album}\n\n{track.QualityText}\n时长 {track.DurationText}", "歌曲信息"); return Task.CompletedTask; });
    }
    private void AddMenu(ContextMenu menu, string label, Func<Task> action)
    {
        var item = new MenuItem { Header = label, Padding = new Thickness(14,9,14,9) };
        item.Click += async (_,_) => await GuardAsync(action); menu.Items.Add(item);
    }
    private async Task AddCurrentToPlaylistAsync()
    {
        if (_emby is null || Current is not { } track || track.Id.StartsWith("radio:")) return;
        var lists = await _emby.GetPlaylistsAsync();
        var picker = new Window { Title = "添加到播放列表", Owner = this, Width = 440, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        picker.SetResourceReference(BackgroundProperty, "Bg"); picker.SetResourceReference(ForegroundProperty, "Text");
        picker.SourceInitialized += (_,_) => _theme.ApplyWindowChrome(picker);
        var stack = new StackPanel { Margin = new Thickness(22) }; var choices = new ComboBox { ItemsSource = lists, DisplayMemberPath = "Name", SelectedIndex = lists.Count > 0 ? 0 : -1 };
        var name = new TextBox { Margin = new Thickness(0,14,0,14) };
        stack.Children.Add(new TextBlock { Text = "选择列表，或输入新列表名称", Margin = new Thickness(0,0,0,16) });stack.Children.Add(choices);stack.Children.Add(name);
        var ok = new Button { Content = "添加", Style = (Style)FindResource("Primary") };ok.Click += (_,_)=>picker.DialogResult=true;stack.Children.Add(ok);picker.Content=stack;
        if(picker.ShowDialog()!=true)return;
        if(name.Text.Trim().Length>0)await _emby.CreatePlaylistAsync(name.Text.Trim(),new[]{track.Id});
        else if(choices.SelectedItem is LibraryItem list)await _emby.AddPlaylistTracksAsync(list.Id,new[]{track.Id});
        else{Status="请选择或新建一个播放列表。";return;}
        Status="已添加到 Emby 播放列表。";
    }
}
