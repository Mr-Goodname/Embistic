using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Embistic.Models;
using Embistic.Views;
using Track = Embistic.Models.Track;

namespace Embistic;

public partial class MainWindow
{
    private sealed record LibraryBrowseState(string Category, string Name, LibraryItem? Container = null);
    private LibraryBrowseState? _libraryState;
    private IReadOnlyList<LibraryItem>? _libraryAllItems;
    private int _libraryOffset, _libraryTotal, _libraryRequestVersion;
    private bool _statsRefreshing, _statsRefreshPending, _statsLoaded, _statsUnavailable;
    private int _statsConnectionVersion = -1;
    private Dictionary<string, FrameworkElement>? _homeModulePanels;
    private TextBlock? _historyEmptyHint;
    private DataTemplate? _compactTrackTemplate;
    private GridLength? _sidebarPlayerExpandedWidth;

    public ObservableCollection<LibraryShortcut> LibraryShortcuts { get; } = new();
    public ObservableCollection<LibraryItem> LibraryItems { get; } = new();
    public ObservableCollection<Track> RecentPlayedTracks { get; } = new();
    public ObservableCollection<Track> FrequentTracks { get; } = new();
    public ObservableCollection<Album> RecentPlayedAlbums { get; } = new();
    public ObservableCollection<Album> FrequentAlbums { get; } = new();

    private async void LibraryShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryShortcut shortcut }) return;
        var origin = CaptureNavigationFrame();
        _searchTimer.Stop();
        _search = "";
        SearchBox.Text = "";
        _searchTimer.Stop();
        _page = 0;
        _albumId = "";
        if (shortcut.Id == "albums") { ShowPageFrom("albums", origin); await GuardAsync(() => LoadAlbumsAsync(false)); return; }
        if (shortcut.Id == "tracks") { ShowPageFrom("tracks", origin); await GuardAsync(LoadTracksAsync); return; }
        await GuardAsync(() => LoadLibraryBrowseAsync(new(shortcut.Id, shortcut.Name), origin));
    }

    private async Task LoadLibraryBrowseAsync(LibraryBrowseState state, NavigationFrame? origin = null)
    {
        var client = _emby;
        if (client is null && state.Category != "radio") { Status = "请先连接 Emby。"; return; }
        var samePage = _section == "browse" && _libraryState == state;
        origin ??= CaptureNavigationFrame();
        var request = ++_libraryRequestVersion;
        var connection = _connectVersion;
        _libraryState = state;
        _libraryAllItems = null;
        _libraryOffset = 0;
        _libraryTotal = 0;
        if (!samePage) ShowPageFrom("browse", origin);
        PageTitle = state.Container?.Name ?? state.Name;
        PageSubtitle = state.Container is { Path.Length: > 0 } container ? container.Path :
            state.Category == "folders" ? "按服务器的真实文件夹层级浏览" : "选择一个条目查看歌曲";
        LibraryBackButton.IsEnabled = true;
        MoreLibraryButton.Visibility = Visibility.Collapsed;
        NewPlaylistButton.Visibility = state.Category == "playlists" ? Visibility.Visible : Visibility.Collapsed;
        NewRadioButton.Visibility = state.Category == "radio" ? Visibility.Visible : Visibility.Collapsed;
        LibraryItems.Clear();
        Status = "正在读取媒体库…";

        IReadOnlyList<LibraryItem> items;
        if (state.Category == "folders")
        {
            var page = await client!.BrowseFolderAsync(state.Container?.Id, 0, 200);
            if (!IsLibraryRequestCurrent(request, connection, client)) return;
            _libraryTotal = page.TotalCount;
            items = page.Items;
        }
        else
        {
            items = state.Category == "playlists" ? await client!.GetPlaylistsAsync()
                : state.Category == "radio" ? RadioLibraryItems()
                : await client!.GetLibraryGroupsAsync(state.Category);
            if (!IsLibraryRequestCurrent(request, connection, client)) return;
            _libraryAllItems = items;
            _libraryTotal = items.Count;
            items = items.Take(200).ToArray();
        }
        Replace(LibraryItems, items);
        _libraryOffset = items.Count;
        UpdateLibraryPaging();
        Status = _libraryTotal == 0 ? state.Category switch
        {
            "playlists" => "还没有播放列表，可创建新列表并加入当前队列。",
            "radio" => "还没有网络电台，点击“添加电台”输入音频流地址。",
            "composers" or "labels" => "服务器音乐元数据中暂未提供这类信息。",
            _ => "这个分类中暂时没有条目。"
        } : $"共 {_libraryTotal:N0} 个条目 · 双击查看或播放";
    }

    private bool IsLibraryRequestCurrent(int request, int connection, Services.EmbyClient? client) =>
        !_closing && request == _libraryRequestVersion && connection == _connectVersion && client == _emby && _section == "browse";

    private void UpdateLibraryPaging()
    {
        MoreLibraryButton.Visibility = _libraryOffset < _libraryTotal ? Visibility.Visible : Visibility.Collapsed;
        MoreLibraryButton.IsEnabled = true;
    }

    private async void MoreLibrary_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_libraryState is null || (_emby is null && _libraryState.Category != "radio") || _section != "browse") return;
        var request = _libraryRequestVersion;
        var connection = _connectVersion;
        var client = _emby;
        MoreLibraryButton.IsEnabled = false;
        try
        {
            IReadOnlyList<LibraryItem> page;
            if (_libraryAllItems is not null) page = _libraryAllItems.Skip(_libraryOffset).Take(200).ToArray();
            else
            {
                var response = await client!.BrowseFolderAsync(_libraryState.Container?.Id, _libraryOffset, 200);
                page = response.Items;
                if (!IsLibraryRequestCurrent(request, connection, client)) return;
                _libraryTotal = response.TotalCount;
            }
            if (!IsLibraryRequestCurrent(request, connection, client)) return;
            foreach (var item in page) LibraryItems.Add(item);
            _libraryOffset += page.Count;
            if (page.Count == 0) _libraryTotal = _libraryOffset;
            UpdateLibraryPaging();
            Status = $"已显示 {LibraryItems.Count:N0} / {_libraryTotal:N0} 个条目";
        }
        finally { if (request == _libraryRequestVersion) MoreLibraryButton.IsEnabled = true; }
    });

    private async void LibraryItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not LibraryItem item) return;
        await GuardAsync(async () =>
        {
            if (item.Track is { } track)
            {
                var visible = LibraryItems.Where(entry => entry.Track is not null).Select(entry => entry.Track!).ToArray();
                await StartQueueAsync(visible, first: track);
                _queueScopeLabel = "当前文件夹页队列"; _queueSourceCount = Queue.Count; Notify(nameof(QueueScopeLabel));
                return;
            }
            if (_emby is null || _libraryState is null) return;
            if (item.Type == "MusicAlbum")
            {
                await OpenAlbumAsync(new Album { Id = item.Id, Title = item.Name, Artist = item.Artist, ImageUrl = item.ImageUrl });
                return;
            }
            if (_libraryState.Category == "folders")
            {
                await LoadLibraryBrowseAsync(new("folders", _libraryState.Name, item));
                return;
            }
            var client = _emby;
            var connection = _connectVersion;
            var request = ++_libraryRequestVersion;
            Status = "正在读取分类中的歌曲…";
            var tracks = await client.GetLibraryItemTracksAsync(item);
            if (!IsLibraryRequestCurrent(request, connection, client)) return;
            ShowCollection(tracks, item.Name, item.Artist);
        });
    }

    private void LibraryBack_Click(object sender, RoutedEventArgs e)
    {
        if (CanNavigateBack) NavigateBack();
        else { _libraryState = null; ShowPage("library"); }
    }

    private async void LibraryPlay_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var client = _emby;
        if (client is null) return;
        var item = LibraryList.SelectedItem as LibraryItem ?? _libraryState?.Container;
        if (item is null && LibraryItems.Count == 1) item = LibraryItems[0];
        if (item is null) { Status = "先选择一个文件夹、分类或播放列表。"; return; }
        var connection = _connectVersion;
        var request = _libraryRequestVersion;
        var selection = await client.GetLibraryItemTracksAsync(item);
        if (!IsLibraryRequestCurrent(request, connection, client)) return;
        await StartQueueAsync(selection);
    });

    private async void CreatePlaylist_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var client = _emby;
        if (client is null) { Status = "请先连接 Emby。"; return; }
        var connection = _connectVersion;
        var tracks = Queue.Where(track => !track.Id.StartsWith("radio:", StringComparison.Ordinal)).Select(track => track.Id).ToArray();
        var name = TextPromptWindow.Ask(this, "新建播放列表", tracks.Length > 0 ? $"名称（加入当前队列的 {tracks.Length:N0} 首音乐）" : "名称（创建空播放列表）");
        if (name is null || _closing || client != _emby || connection != _connectVersion) return;
        Status = "正在创建 Emby 播放列表…";
        var created = await client.CreatePlaylistAsync(name, tracks);
        if (_closing || client != _emby || connection != _connectVersion) return;
        if (_section == "browse" && _libraryState?.Category == "playlists") await LoadLibraryBrowseAsync(_libraryState);
        Status = $"已创建播放列表 · {created.Name}";
    });

    private void AddRadio_Click(object sender, RoutedEventArgs e)
    {
        var entry = TextPromptWindow.AskTwo(this, "添加网络电台", "电台名称", "音频流地址（http / https）");
        if (entry is null) return;
        if (!Uri.TryCreate(entry.Second, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(url.UserInfo) || entry.Second.Length > 4096)
        { Status = "请输入有效的 http 或 https 音频流地址。"; return; }
        _settings.Display.RadioStations.Add(new RadioStation { Name = entry.First, Url = url.AbsoluteUri });
        _settings.Display.Normalize();
        SaveSettings();
        if (_section == "browse" && _libraryState?.Category == "radio")
        {
            _libraryAllItems = RadioLibraryItems();
            _libraryTotal = _libraryAllItems.Count;
            Replace(LibraryItems, _libraryAllItems.Take(200));
            _libraryOffset = LibraryItems.Count;
            UpdateLibraryPaging();
        }
        Status = $"已添加电台 · {entry.First}";
    }

    private IReadOnlyList<LibraryItem> RadioLibraryItems() => _settings.Display.RadioStations.Select(station =>
    {
        var id = "radio:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(station.Url)))[..20].ToLowerInvariant();
        var track = new Track { Id = id, Title = station.Name, Artist = "网络电台", Path = station.Url, Container = "radio" };
        return new LibraryItem { Id = id, Name = station.Name, Artist = Uri.TryCreate(station.Url, UriKind.Absolute, out var uri) ? uri.Host : "", Path = station.Url, Type = "Radio", IsFolder = false, Track = track };
    }).ToArray();

    private void ShowCollection(IReadOnlyList<Track> tracks, string title, string subtitle)
    {
        var origin = CaptureNavigationFrame();
        _search = ""; _albumId = ""; _page = 0;
        SearchBox.Text = ""; _searchTimer.Stop();
        _collectionTracks = tracks.ToArray();
        PageTitle = title;
        PageSubtitle = string.IsNullOrWhiteSpace(subtitle) ? $"{tracks.Count:N0} 首歌曲" : $"{subtitle} · {tracks.Count:N0} 首歌曲";
        ShowPageFrom("collection", origin);
        _ = RefreshCollectionPage();
    }

    private Task RefreshCollectionPage()
    {
        if (_collectionTracks is null) return Task.CompletedTask;
        _resultCount = _collectionTracks.Count;
        _page = Math.Clamp(_page, 0, Math.Max(0, (_resultCount - 1) / 200));
        Replace(Tracks, _collectionTracks.Skip(_page * 200).Take(200));
        PreviousPageButton.IsEnabled = _page > 0;
        NextPageButton.IsEnabled = (_page + 1) * 200 < _resultCount;
        Notify(nameof(PagingLabel));
        Status = _resultCount == 0 ? "这个分类中还没有歌曲。" : $"共 {_resultCount:N0} 首 · 播放全部会读取完整分类";
        return Task.CompletedTask;
    }

    private async void HomeTrack_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not Track track) return;
        if (sender is ListBox { ItemsSource: IEnumerable<Track> source })
            await GuardAsync(() => StartQueueAsync(source, first: track));
    }

    private async Task RefreshListeningStatsAsync()
    {
        var client = _emby;
        if (client is null || _closing) return;
        var connection = _connectVersion;
        if (_statsConnectionVersion != connection)
        {
            _statsConnectionVersion = connection; _statsLoaded = false; _statsUnavailable = false;
            RecentPlayedTracks.Clear(); FrequentTracks.Clear(); RecentPlayedAlbums.Clear(); FrequentAlbums.Clear();
            ApplyHomeModules();
        }
        if (_statsRefreshing) { _statsRefreshPending = true; return; }
        _statsRefreshing = true;
        try
        {
            var stats = await client.GetListeningStatsAsync();
            if (_closing || client != _emby || connection != _connectVersion) return;
            Replace(RecentPlayedTracks, RestoreHistoryRadioTracks(stats.RecentTracks));
            Replace(FrequentTracks, RestoreHistoryRadioTracks(stats.MostPlayedTracks));
            Replace(RecentPlayedAlbums, stats.RecentAlbums); Replace(FrequentAlbums, stats.MostPlayedAlbums);
            _statsLoaded = true; _statsUnavailable = false;
            ApplyHomeModules();
        }
        catch
        {
            if (!_closing && client == _emby && connection == _connectVersion) { _statsUnavailable = true; ApplyHomeModules(); }
        }
        finally
        {
            _statsRefreshing = false;
            if (_statsRefreshPending && !_closing) { _statsRefreshPending = false; _ = RefreshListeningStatsAsync(); }
        }
    }

    private IReadOnlyList<Track> RestoreHistoryRadioTracks(IReadOnlyList<Track> tracks)
    {
        var stations = RadioLibraryItems().Select(item => item.Track!).DistinctBy(track => track.Id).ToDictionary(track => track.Id, StringComparer.Ordinal);
        return tracks.Select(track => track.Id.StartsWith("radio:", StringComparison.Ordinal)
                ? stations.GetValueOrDefault(track.Id) : track)
            .Where(track => track is not null).Select(track => track!).ToArray();
    }

    private void ApplyHomeModules()
    {
        _homeModulePanels ??= HomeModules.Children.OfType<FrameworkElement>().Where(panel => panel.Tag is string).ToDictionary(panel => (string)panel.Tag);
        _historyEmptyHint ??= new TextBlock { FontSize = 12, Margin = new Thickness(0, 18, 0, 24), TextWrapping = TextWrapping.Wrap, LineHeight = 22 };
        _historyEmptyHint.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var chosen = _settings.Display.HomeSections;
        var ordered = chosen.Where(_homeModulePanels.ContainsKey).Concat(_homeModulePanels.Keys.Except(chosen)).Select(id => _homeModulePanels[id]).Append(_historyEmptyHint).ToArray();
        if (!HomeModules.Children.OfType<FrameworkElement>().SequenceEqual(ordered))
        {
            HomeModules.Children.Clear();
            foreach (var panel in ordered) HomeModules.Children.Add(panel);
        }
        foreach (var (id, panel) in _homeModulePanels)
        {
            var populated = id switch
            {
                "favorites" => FavoriteAlbums.Count > 0, "recentAlbums" => RecentAlbums.Count > 0,
                "recentTracks" => RecentPlayedTracks.Count > 0, "frequentTracks" => FrequentTracks.Count > 0,
                "playedAlbums" => RecentPlayedAlbums.Count > 0, "frequentAlbums" => FrequentAlbums.Count > 0,
                _ => false
            };
            panel.Visibility = chosen.Contains(id) && populated ? Visibility.Visible : Visibility.Collapsed;
        }
        var historySelected = chosen.Any(id => id is "recentTracks" or "frequentTracks" or "playedAlbums" or "frequentAlbums");
        _historyEmptyHint.Visibility = historySelected && RecentPlayedTracks.Count + FrequentTracks.Count + RecentPlayedAlbums.Count + FrequentAlbums.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _historyEmptyHint.Text = _statsUnavailable ? "播放记录暂时无法读取，可稍后刷新。" : !_statsLoaded && _emby is not null ? "正在读取播放记录…" :
            "还没有播放记录。收听音乐后，这里会显示最近与最常播放的歌曲和专辑。";
    }

    private void ApplyDisplaySettings()
    {
        _settings.Display.Normalize();
        var shortcuts = new Dictionary<string, LibraryShortcut>
        {
            ["albums"] = new("albums", "专辑", "\uE93C"), ["albumArtists"] = new("albumArtists", "专辑艺术家", "\uE77B"),
            ["genres"] = new("genres", "流派", "\uE8EC"), ["tracks"] = new("tracks", "曲目", "\uE8D6"),
            ["playlists"] = new("playlists", "播放列表", "\uE8EF"), ["folders"] = new("folders", "文件夹", "\uE8B7"),
            ["radio"] = new("radio", "网络电台", "\uEC6A"), ["composers"] = new("composers", "作曲家", "\uE70F"),
            ["artists"] = new("artists", "所有艺术家", "\uE716"), ["compilations"] = new("compilations", "合辑", "\uE93C"),
            ["years"] = new("years", "年份", "\uE787"), ["labels"] = new("labels", "音乐厂牌", "\uE8D4")
        };
        Replace(LibraryShortcuts, _settings.Display.LibraryCategories.Where(shortcuts.ContainsKey).Select(id => shortcuts[id]));
        var template = _settings.Display.CompactTrackRows ? _compactTrackTemplate ??= CreateCompactTrackTemplate() : (DataTemplate)FindResource("TrackRow");
        TrackList.ItemTemplate = template; QueueList.ItemTemplate = template;
        foreach (var list in HomeModules.Children.OfType<Panel>().SelectMany(panel => panel.Children.OfType<ListBox>())) list.ItemTemplate = template;
        ImmersivePlayer.DiscAnimationEnabled = _settings.Display.DiscAnimation;
        ImmersivePlayer.CoverPresentation = _settings.Display.CoverPresentation;
        SidebarPlayer.Visibility = _settings.Display.ShowSidebarPlayer ? Visibility.Visible : Visibility.Collapsed;
        if (SidebarPlayer.Parent is Grid layout)
        {
            var column = layout.ColumnDefinitions[Grid.GetColumn(SidebarPlayer)];
            _sidebarPlayerExpandedWidth ??= column.Width;
            column.Width = _settings.Display.ShowSidebarPlayer ? _sidebarPlayerExpandedWidth.Value : new GridLength(0);
        }
        ApplyHomeModules();
    }

    private DataTemplate CreateCompactTrackTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 4, 10, 4));
        var add = new FrameworkElementFactory(typeof(Button));
        add.SetValue(DockPanel.DockProperty, Dock.Right);
        add.SetValue(ContentControl.ContentProperty, "\uE710");
        add.SetValue(FrameworkElement.StyleProperty, (Style)FindResource("IconButton"));
        add.SetValue(Control.FontSizeProperty, 13.0);
        add.SetValue(Control.PaddingProperty, new Thickness(7));
        add.SetValue(FrameworkElement.ToolTipProperty, "加入播放队列");
        add.AddHandler(Button.ClickEvent, new RoutedEventHandler(AddQueue_Click));
        row.AppendChild(add);
        var duration = new FrameworkElementFactory(typeof(TextBlock));
        duration.SetValue(DockPanel.DockProperty, Dock.Right);
        duration.SetValue(FrameworkElement.WidthProperty, 58.0);
        duration.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        duration.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        duration.SetValue(TextBlock.FontSizeProperty, 11.0);
        duration.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        duration.SetBinding(TextBlock.TextProperty, new Binding("DurationText"));
        row.AppendChild(duration);
        var album = new FrameworkElementFactory(typeof(TextBlock));
        album.SetValue(DockPanel.DockProperty, Dock.Right);
        album.SetValue(FrameworkElement.WidthProperty, 158.0);
        album.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        album.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        album.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 10, 0));
        album.SetValue(TextBlock.FontSizeProperty, 11.0);
        album.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        album.SetBinding(TextBlock.TextProperty, new Binding("Album"));
        row.AppendChild(album);
        var art = new FrameworkElementFactory(typeof(Border));
        art.SetValue(DockPanel.DockProperty, Dock.Left);
        art.SetValue(FrameworkElement.WidthProperty, 32.0); art.SetValue(FrameworkElement.HeightProperty, 32.0);
        art.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
        art.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        art.SetResourceReference(Border.BackgroundProperty, "Raised");
        var image = new FrameworkElementFactory(typeof(Image));
        image.SetValue(Image.StretchProperty, Stretch.UniformToFill);
        image.SetBinding(Image.SourceProperty, new Binding("ImageUrl") { Converter = (IValueConverter)FindResource("Artwork") });
        art.AppendChild(image); row.AppendChild(art);
        var text = new FrameworkElementFactory(typeof(StackPanel));
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetValue(TextBlock.FontSizeProperty, 12.0); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetBinding(TextBlock.TextProperty, new Binding("Title"));
        var artist = new FrameworkElementFactory(typeof(TextBlock));
        artist.SetValue(TextBlock.FontSizeProperty, 10.5); artist.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        artist.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        artist.SetBinding(TextBlock.TextProperty, new Binding("Artist"));
        text.AppendChild(title); text.AppendChild(artist); row.AppendChild(text);
        return new DataTemplate(typeof(Track)) { VisualTree = row };
    }
}
