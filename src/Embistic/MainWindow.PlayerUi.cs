using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Embistic.Models;
using Embistic.Views;

namespace Embistic;

public partial class MainWindow
{
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private WindowState _previousWindowState;
    private bool _fullscreen;
    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _previousWindowStyle = WindowStyle; _previousResizeMode = ResizeMode; _previousWindowState = WindowState;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
        else { WindowState = WindowState.Normal; WindowStyle = _previousWindowStyle; ResizeMode = _previousResizeMode; WindowState = _previousWindowState; }
        _fullscreen = !_fullscreen;
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
        AddMenu(menu, "从待播队列移除同名歌曲", () => { for(var i=Queue.Count-1;i>=0;i--)if(Queue[i].Id==track.Id&&i!=_queueIndex){Queue.RemoveAt(i);if(i<_queueIndex)_queueIndex--;} return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        AddMenu(menu, track.IsFavorite ? "取消收藏" : "添加到收藏", () => ToggleFavoriteAsync(track));
        AddMenu(menu, "添加到播放列表", () => AddCurrentToPlaylistAsync());
        AddMenu(menu, "专辑混播", async () => { if(_emby is null)return; var result=await _emby.GetTracksAsync(albumId:track.AlbumId,limit:1000);await StartQueueAsync(result.Items,true); });
        menu.Items.Add(new Separator());
        AddMenu(menu, "转到专辑", async () => { if(_emby is null||track.AlbumId.Length==0)return; _albumId=track.AlbumId;_page=0;_search="";PageTitle=track.Album;PageSubtitle=track.Artist;ShowPage("album");await LoadTracksAsync(); });
        AddMenu(menu, "转到艺术家", async () => { if(_emby is null)return; var artists=await _emby.GetArtistsAsync();var item=artists.FirstOrDefault(a=>string.Equals(a.Name,track.Artist,StringComparison.CurrentCultureIgnoreCase));if(item is not null)ShowCollection(await _emby.GetLibraryItemTracksAsync(item),item.Name,"艺术家");else{SearchBox.Text=track.Artist;await ApplySearchAsync();} });
        AddMenu(menu, "歌词", () => { ShowPage("lyrics"); return Task.CompletedTask; });
        AddMenu(menu, "下载到此电脑", DownloadCurrentAsync);
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
