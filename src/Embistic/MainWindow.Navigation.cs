using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Embistic.Models;

namespace Embistic;

public partial class MainWindow
{
    private sealed record NavigationFrame(
        string Section, string Root, string Title, string Subtitle, string Status,
        string AlbumId, string Search, string SearchText, int Page, int ResultCount, int AlbumStart,
        Track[]? Tracks, Album[]? Albums, IReadOnlyList<Track>? Collection,
        LibraryBrowseState? LibraryState, IReadOnlyList<LibraryItem>? LibraryAllItems,
        LibraryItem[]? LibraryItems, int LibraryOffset, int LibraryTotal,
        double ScrollX, double ScrollY, object? Selection, int Connection);

    private readonly List<NavigationFrame> _navigationHistory = new();
    private NavigationFrame? _immersiveReturnFrame;
    private string _navigationRoot = "home";
    private bool _restoringNavigation, _navigatingFromFrame, _syncingNavigationSelection;
    private int _navigationVersion;
    public bool CanNavigateBack => _section == "lyrics" || _navigationHistory.Count > 0;

    private void InitializeNavigation()
    {
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack,
            (_, e) => { NavigateBack(); e.Handled = true; },
            (_, e) => { e.CanExecute = CanNavigateBack; e.Handled = true; }));
        InputBindings.Add(new KeyBinding(NavigationCommands.BrowseBack, new KeyGesture(Key.Left, ModifierKeys.Alt)));
        InputBindings.Add(new KeyBinding(NavigationCommands.BrowseBack, new KeyGesture(Key.BrowserBack)));
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1 && CanNavigateBack) { NavigateBack(); e.Handled = true; }
        };
    }

    private NavigationFrame CaptureNavigationFrame()
    {
        if (_section == "lyrics" && _immersiveReturnFrame is { } original) return original;
        var scroller = NavigationScroller(_section);
        return new(_section, _navigationRoot, PageTitle, PageSubtitle, Status, _albumId, _search,
            SearchBox.Text, _page, _resultCount, _albumStart,
            _section is "tracks" or "favorites" or "album" or "collection" ? Tracks.ToArray() : null,
            _section == "albums" ? Albums.ToArray() : null, _collectionTracks,
            _libraryState, _libraryAllItems, _section == "browse" ? LibraryItems.ToArray() : null,
            _libraryOffset, _libraryTotal, scroller?.HorizontalOffset ?? 0, scroller?.VerticalOffset ?? 0,
            _section == "browse" ? LibraryList.SelectedItem : _section == "queue" ? QueueList.SelectedItem : TrackList.SelectedItem,
            _connectVersion);
    }

    // Callers capture before replacing filters, headings or loaded results. In
    // particular an album click changes all three before ShowPage is reached.
    private void ShowPageFrom(string section, NavigationFrame origin)
    {
        if (origin.Connection == _connectVersion && !(origin.Section == "album" && section == "album" && origin.AlbumId == _albumId))
        {
            if (_navigationHistory.Count == 32) _navigationHistory.RemoveAt(0);
            _navigationHistory.Add(origin);
        }
        _navigationRoot = origin.Root;
        _navigatingFromFrame = true;
        try { ShowPage(section); }
        finally { _navigatingFromFrame = false; }
        RestoreNavigationScroll(section, 0, 0, ++_navigationVersion);
    }

    private bool RestoreImmersiveReturn(string section)
    {
        if (_restoringNavigation || _navigatingFromFrame || _section != "lyrics"
            || _immersiveReturnFrame is not { } frame || section != frame.Section || frame.Connection != _connectVersion) return false;
        _immersiveReturnFrame = null;
        RestoreNavigationFrame(frame);
        return true;
    }

    private void PreparePageNavigation(string section)
    {
        ++_navigationVersion;
        if (_restoringNavigation) return;
        if (section == "lyrics")
        {
            if (_section != "lyrics") _immersiveReturnFrame = CaptureNavigationFrame();
            return;
        }
        if (_navigatingFromFrame) return;
        _navigationHistory.Clear();
        _navigationRoot = RootSection(section);
        _immersiveReturnFrame = null;
    }

    private static string RootSection(string section) => section switch
    {
        "browse" => "library", "album" => "albums", "collection" => "home", "lyrics" => "home", _ => section
    };

    private void UpdateNavigationUi()
    {
        _syncingNavigationSelection = true;
        try
        {
            foreach (var button in NavigationChildren<RadioButton>(WindowFrame).Where(b => b.GroupName == "Nav"))
                button.IsChecked = (button.Tag as string) == _navigationRoot;
        }
        finally { _syncingNavigationSelection = false; }
        Notify(nameof(CanNavigateBack)); CommandManager.InvalidateRequerySuggested();
    }

    private void NavigationBack_Click(object sender, RoutedEventArgs e) => NavigateBack();
    private void NavigateBack()
    {
        if (_section == "lyrics")
        {
            ShowPage(_immersiveReturnFrame?.Section ?? _previousSection);
            return;
        }
        while (_navigationHistory.Count > 0)
        {
            var frame = _navigationHistory[^1]; _navigationHistory.RemoveAt(_navigationHistory.Count - 1);
            if (frame.Connection != _connectVersion) continue;
            _albumQueueCts?.Cancel(); ++_albumQueueVersion;
            RestoreNavigationFrame(frame);
            return;
        }
        UpdateNavigationUi();
    }

    private void RestoreNavigationFrame(NavigationFrame frame)
    {
        FlushInlineSettings(); _searchTimer.Stop(); _pageCts?.Cancel(); ++_libraryRequestVersion;
        _restoringNavigation = true;
        try
        {
            _navigationRoot = frame.Root;
            _albumId = frame.AlbumId; _search = frame.Search; _page = frame.Page;
            _resultCount = frame.ResultCount; _albumStart = frame.AlbumStart; _collectionTracks = frame.Collection;
            _libraryState = frame.LibraryState; _libraryAllItems = frame.LibraryAllItems;
            _libraryOffset = frame.LibraryOffset; _libraryTotal = frame.LibraryTotal;
            SearchBox.Text = frame.SearchText; _searchTimer.Stop();
            if (frame.Tracks is not null) Replace(Tracks, frame.Tracks);
            if (frame.Albums is not null) Replace(Albums, frame.Albums);
            if (frame.LibraryItems is not null) Replace(LibraryItems, frame.LibraryItems);
            ShowPage(frame.Section);
            PageTitle = frame.Title; PageSubtitle = frame.Subtitle; Status = frame.Status;
            PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = (_page + 1) * 200 < _resultCount;
            MoreAlbumsButton.Visibility = Albums.Count > 0 && Albums.Count % 48 == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateLibraryPaging();
            NewPlaylistButton.Visibility = _libraryState?.Category == "playlists" ? Visibility.Visible : Visibility.Collapsed;
            NewRadioButton.Visibility = _libraryState?.Category == "radio" ? Visibility.Visible : Visibility.Collapsed;
            if (frame.Section == "browse") LibraryList.SelectedItem = frame.Selection;
            else if (frame.Section == "queue") QueueList.SelectedItem = frame.Selection;
            else TrackList.SelectedItem = frame.Selection;
            Notify(nameof(PagingLabel));
        }
        finally { _restoringNavigation = false; }
        UpdateNavigationUi();
        RestoreNavigationScroll(frame.Section, frame.ScrollX, frame.ScrollY, ++_navigationVersion);
    }

    private void RestoreNavigationScroll(string section, double x, double y, int version)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_closing || version != _navigationVersion || _section != section) return;
            PageTransitionHost.UpdateLayout();
            var scroll = NavigationScroller(section);
            if (scroll is null) return;
            scroll.ScrollToHorizontalOffset(x); scroll.ScrollToVerticalOffset(y);
        });
    }

    private ScrollViewer? NavigationScroller(string section) => section switch
    {
        "home" => HomePage, "albums" => AlbumsPage, "library" => LibraryOverviewPage,
        "settings" => SettingsPage, "devices" => DevicesPage,
        "browse" => NavigationChildren<ScrollViewer>(LibraryList).FirstOrDefault(),
        "queue" => NavigationChildren<ScrollViewer>(QueueList).FirstOrDefault(),
        "tracks" or "favorites" or "album" or "collection" => NavigationChildren<ScrollViewer>(TrackList).FirstOrDefault(),
        _ => null
    };

    private static IEnumerable<T> NavigationChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in NavigationChildren<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private async Task NavigateRootAsync(string section)
    {
        _navigationHistory.Clear(); _navigationRoot = section; _immersiveReturnFrame = null;
        ++_libraryRequestVersion;
        _searchTimer.Stop(); _search = ""; SearchBox.Text = ""; _searchTimer.Stop(); _albumId = ""; _page = 0;
        ShowPage(section);
        RestoreNavigationScroll(section, 0, 0, ++_navigationVersion);
        await GuardAsync(() => LoadSectionAsync(section));
    }

    private async void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _syncingNavigationSelection || sender is not RadioButton { Tag: string section }) return;
        // Checked runs first when changing tabs. This path handles clicking the
        // already-selected root while an album, search or library child is open.
        if (_section != section || _search.Length > 0 || _navigationHistory.Count > 0) await NavigateRootAsync(section);
    }
}
