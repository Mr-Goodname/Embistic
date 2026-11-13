using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;
using Embistic.Controls;
using Microsoft.Win32;
using Track = Embistic.Models.Track;

namespace Embistic;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private AppSettings _settings = SettingsService.Load();
    private Action<AppSettings> _saveSettings = SettingsService.Save;
    private EmbyClient? _emby;
    private PlaybackService? _player;
    private readonly DlnaService _dlna = new();
    private readonly LyricsService _lyrics = new();
    private DesktopLyricsWindow? _desktop;
    private DlnaDevice? _renderer;
    private LyricsDocument _document = LyricsDocument.Empty();
    private LyricAppearanceSettings _appearance = new();
    private AnimatedLyricsView AnimatedLyrics => ImmersivePlayer.LyricsView;
    private readonly ThemeService _theme = new();
    private readonly AudioSpectrumService _spectrumService = new();
    private SpectrumSettings _spectrumSettings = new();
    private ListeningHistoryService? _history;
    private DownloadService? _downloads;
    private bool _spectrumAttempted;
    private IReadOnlyList<Track>? _collectionTracks;
    private string _previousSection = "home";
    private (string Title, string Subtitle)? _lyricsReturnHeading;
    private bool _appearancePreviewActive;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _volumeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly SemaphoreSlim _transportGate = new(1, 1);
    private readonly SemaphoreSlim _favoriteGate = new(1, 1);
    private CancellationTokenSource? _queueSelectionCts;
    private Task _sessionReports = Task.CompletedTask;
    private int _automaticAdvanceVersion = -1;
    private int _queueSourceCount;
    private string _queueScopeLabel = "自定义队列";
    private CancellationTokenSource? _pageCts, _lyricCts;
    private bool _ready, _closing, _allowClose, _seeking, _playing, _shuffle, _polling, _reporting;
    private int _repeat, _page, _totalTracks, _albumCount, _queueIndex = -1, _lyricIndex = -2, _playVersion;
    private int _resultCount, _tick, _albumStart, _connectVersion;
    private long _position, _duration;
    private DateTime _remoteStarted;
    private bool _remoteHasPlayed;
    private bool _switchingOutput;
    private int _outputSwitchVersion;
    private (Track? Track, int Version, long Position, bool Playing)? _outputHandoff;
    private string _section = "home", _albumId = "", _search = "";
    private string _pageTitle = "为你推荐", _pageSubtitle = "让每一次打开，都有好音乐相伴。";
    private string _status = "正在连接 Emby…", _connectionLabel = "○  正在连接", _dlnaStatus = "点击搜索，发现同一局域网的 DLNA 音箱。";
    private Track? _current;

    public ObservableCollection<Track> Tracks { get; } = new();
    public ObservableCollection<Track> Queue { get; } = new();
    public ObservableCollection<Album> RecentAlbums { get; } = new();
    public ObservableCollection<Album> FavoriteAlbums { get; } = new();
    public ObservableCollection<Album> Albums { get; } = new();
    public ObservableCollection<AudioDevice> AudioDevices { get; } = new();
    public ObservableCollection<DlnaDevice> DlnaDevices { get; } = new();
    public ObservableCollection<EmbyUser> Users { get; } = new();
    public ObservableCollection<LyricsLine> LyricLines { get; } = new();
    public Track? Current { get => _current; private set { _current = value; Notify(); NotifyNowPlaying(); } }
    public string PageTitle { get => _pageTitle; set { _pageTitle = value; Notify(); } }
    public string PageSubtitle { get => _pageSubtitle; set { _pageSubtitle = value; Notify(); } }
    public string Status { get => _status; set { _status = value; Notify(); } }
    public string ConnectionLabel { get => _connectionLabel; set { _connectionLabel = value; Notify(); } }
    public string DlnaStatus { get => _dlnaStatus; set { _dlnaStatus = value; Notify(); } }
    public string LibraryCountLabel => _emby is null ? "连接你的音乐收藏" : $"{_totalTracks:N0} 首歌曲 · {_albumCount:N0} 张专辑";
    public string CurrentTitle => Current?.Title ?? "今天，想听点什么？";
    public string CurrentArtist => Current?.Artist ?? "从你的音乐库开始";
    public string QualityLabel => Current is null ? "EMBY MUSIC" : string.IsNullOrWhiteSpace(Current.QualityText) ? "DIRECT PLAY" : Current.QualityText;
    public string FavoriteGlyph => Current?.IsFavorite == true ? "\uEB52" : "\uEB51";
    public string PlayGlyph => _playing ? "\uE769" : "\uE768";
    public string PositionLabel => FormatTime(_position);
    public string DurationLabel => FormatTime(_duration);
    public string VolumeLabel => ((int)(VolumeSlider?.Value ?? _settings.Volume)).ToString();
    public string OutputLabel => _renderer?.Name ?? AudioDevices.FirstOrDefault(d => d.Id == _settings.AudioDeviceId)?.Name ?? "系统默认输出";
    public Brush ShuffleBrush => _shuffle ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
    public Brush RepeatBrush => _repeat != 0 ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
    public string RepeatGlyph => _repeat == 2 ? "\uE8ED" : "\uE8EE";
    public string RepeatLabel => _repeat switch { 1 => "列表循环", 2 => "单曲循环", _ => "顺序播放" };
    public string PagingLabel => _resultCount == 0 ? "0 首" : $"{_page + 1} / {Math.Max(1, (int)Math.Ceiling(_resultCount / 200.0))} 页 · {_resultCount:N0} 首";
    public string QueueScopeLabel => Queue.Count != _queueSourceCount ? "自定义队列" : _queueScopeLabel;
    public bool HasFavoriteAlbums => FavoriteAlbums.Count > 0;
    public bool HasPlainLyrics => !_document.IsSynced;
    public string PlainLyrics => _document.IsSynced ? "" : _document.PlainText;
    public string LyricsInfo => $"{(string.IsNullOrEmpty(_document.Source) ? "自动获取歌词" : _document.Source)}  ·  偏移 {_settings.LyricsOffsetMs / 1000:+0.00;-0.00;0.00} s" + (_document.IsSynced ? "  ·  双击歌词可跳转" : "");
    public string CurrentLyric => Current is null ? "让旋律，慢慢展开。" : _document.IsSynced && _lyricIndex >= 0 && _lyricIndex < _document.Lines.Count ? _document.Lines[_lyricIndex].Text : _document.IsSynced ? "前奏 · 等待歌词" : _document.IsInstrumental ? "纯音乐，请欣赏" : _document.PlainText.Split('\n').FirstOrDefault() ?? "暂未找到歌词";
    public string NextLyric => _document.IsSynced && _lyricIndex + 1 >= 0 && _lyricIndex + 1 < _document.Lines.Count ? _document.Lines[_lyricIndex + 1].Text : Current is null ? "播放后自动获取同步歌词" : "";
    public string DesktopLyricsLabel => _settings.DesktopLyricsEnabled ? "关闭桌面歌词" : "开启桌面歌词";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void NotifyNowPlaying()
    {
        foreach (var p in new[] { nameof(CurrentTitle), nameof(CurrentArtist), nameof(QualityLabel), nameof(FavoriteGlyph), nameof(PlayGlyph), nameof(OutputLabel), nameof(CurrentLyric), nameof(NextLyric) }) Notify(p);
        ImmersivePlayer.SetTrack(Current);
        ImmersivePlayer.UpdatePlayback(_position, _playing);
        AnimatedLyrics.UpdatePlayback(_position, _playing, _settings.LyricsOffsetMs);
        _desktop?.UpdatePlayback(_position, _playing);
        _tray?.Update(CurrentTitle, _playing);
        SyncSpectrumCapture();
    }
    private static string FormatTime(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms)).ToString(ms >= 3600000 ? @"h\:mm\:ss" : @"m\:ss");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _theme.ThemeChanged += () => { Notify(nameof(ShuffleBrush)); Notify(nameof(RepeatBrush)); ApplyAppearance(_appearance); };
        _theme.Apply(_settings.Theme);
        ApplyAppearance(_settings.Appearance);
        ApplySpectrumSettings(_settings.Spectrum);
        ApplyDisplaySettings();
        _spectrumService.SpectrumReady += bins => ImmersivePlayer.SetSpectrum(bins, _spectrumService.SampleRate);
        _spectrumService.StatusChanged += text => Dispatcher.BeginInvoke(() => { if (!_closing) ImmersivePlayer.SetSpectrumStatus(text); });
        ImmersivePlayer.BackRequested += () => ShowPage(_previousSection == "lyrics" ? "home" : _previousSection);
        ImmersivePlayer.FullscreenRequested += ToggleFullscreen;
        ImmersivePlayer.LyricsSettingsRequested += () => Appearance_Click(this, new RoutedEventArgs());
        ImmersivePlayer.SpectrumSettingsRequested += () => SpectrumSettings_Click(this, new RoutedEventArgs());
        ImmersivePlayer.ImportLyricsRequested += () => ImportLyrics_Click(this, new RoutedEventArgs());
        ImmersivePlayer.DesktopLyricsRequested += () => SetDesktopLyrics(!_settings.DesktopLyricsEnabled);
        ImmersivePlayer.OffsetChangeRequested += delta => SetOffset(_settings.LyricsOffsetMs + delta);
        ImmersivePlayer.OffsetResetRequested += () => SetOffset(0);
        ImmersivePlayer.SetLyricsOffset(_settings.LyricsOffsetMs);
        AnimatedLyrics.SeekRequested += async timestamp => await GuardAsync(() => SeekAsync((long)(timestamp - _settings.LyricsOffsetMs)));
        ServerBox.Text = _settings.ServerUrl;
        ApiKeyBox.Password = _settings.ApiKey;
        UsernameBox.Text = _settings.UserName;
        LoginModeBox.SelectedValue = _settings.LoginMode;
        AutoUpdateBox.IsChecked = _settings.CheckUpdatesAutomatically;
        _lyrics.ConfigureSources(_settings.LyricsSources);
        Users.Add(new EmbyUser(_settings.UserId, string.IsNullOrEmpty(_settings.UserName) ? "尚未连接" : _settings.UserName));
        UsersBox.SelectedIndex = 0;
        if (!_settings.Display.RememberVolume) _settings.Volume = _settings.Display.StartupVolume;
        VolumeSlider.Value = _settings.Volume;
        SeekBar.PreviewMouseLeftButtonDown -= Seek_Down;
        SeekBar.PreviewMouseLeftButtonUp -= Seek_Up;
        SeekBar.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Seek_Down), true);
        SeekBar.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Seek_Up), true);
        SeekBar.LostMouseCapture += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (!_seekReleasePending && !SeekBar.IsMouseCaptureWithin) _seeking = false; });
        InitializeLocalStores();
        DesktopEnabledBox.IsChecked = _settings.DesktopLyricsEnabled;
        DesktopLockedBox.IsChecked = _settings.LyricsLocked;
        _timer.Tick += Timer_Tick;
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await ApplySearchAsync(); };
        _volumeTimer.Tick += async (_, _) =>
        {
            _volumeTimer.Stop();
            SaveSettings();
            if (_renderer is { } device) await GuardAsync(async () => await _dlna.SetVolumeAsync(device, _settings.Volume));
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _player = new PlaybackService { Volume = _settings.Volume };
            _player.PlaybackEnded += () => Dispatcher.InvokeAsync(async () => { if (!_closing && _renderer is null) await AdvanceAsync(true); });
            _player.Error += message => Dispatcher.InvokeAsync(() => { if (_closing || _renderer is not null) return; _playing = false; NotifyNowPlaying(); Status = message; });
            RefreshAudioDevices();
        }
        catch (Exception ex) { Status = "本地播放引擎初始化失败：" + SafeMessage(ex); }
        _ready = true;
        _timer.Start();
        if (_settings.DesktopLyricsEnabled) SetDesktopLyrics(true);
        if (!string.IsNullOrWhiteSpace(_settings.ServerUrl) && !string.IsNullOrWhiteSpace(_settings.ApiKey))
            await GuardAsync(ConnectAsync);
        else { ShowPage("settings"); Status = SettingsService.LastLoadWarning ?? "填写 Emby 地址，使用账号密码或 API 密钥连接。"; }
        if (_settings.CheckUpdatesAutomatically) _ = CheckUpdatesQuietlyAsync();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _theme.ApplyWindowChrome(this);
        InitializeTray();
    }

    private async Task ConnectAsync()
    {
        var version = ++_connectVersion;
        Status = "正在连接 Emby 并读取音乐库…";
        var client = new EmbyClient(_settings);
        try
        {
            IReadOnlyList<EmbyUser> users = _settings.LoginMode == "Password" && !string.IsNullOrEmpty(_settings.UserId)
                ? new[] { new EmbyUser(_settings.UserId, _settings.UserName) }
                : await client.GetUsersAsync();
            if (version != _connectVersion) { client.Dispose(); return; }
            var user = users.FirstOrDefault(u => u.Id == _settings.UserId) ?? users.FirstOrDefault() ?? throw new InvalidOperationException("服务器没有可用用户。");
            _settings.UserId = user.Id; _settings.UserName = user.Name;
            client.Dispose(); client = new EmbyClient(_settings);
            Replace(Users, users); UsersBox.SelectedValue = user.Id;
            var countTask = client.GetTracksAsync(limit: 1);
            var albumTask = client.GetAlbumCountAsync();
            await Task.WhenAll(countTask, albumTask);
            if (version != _connectVersion) { client.Dispose(); return; }
            _emby?.Dispose(); _emby = client;
            InitializeLocalStores();
            _totalTracks = countTask.Result.TotalCount; _albumCount = albumTask.Result;
            ConnectionLabel = "●  Emby 已连接";
            Notify(nameof(LibraryCountLabel));
            SaveSettings();
            await LoadHomeAsync();
            ShowPage(_settings.Display.StartInLyrics ? "lyrics" : "home");
            HomeNav.IsChecked = true;
            Status = $"已连接 · {user.Name} · 音乐库已就绪";
        }
        catch { if (_emby != client) client.Dispose(); ConnectionLabel = "○  连接未完成"; throw; }
    }

    private void InitializeLocalStores()
    {
        _history = new ListeningHistoryService(_settings);
        _history.Changed += () => { if (!_closing && _section == "home") _ = RefreshListeningStatsAsync(); };
        _downloads?.Dispose();
        _downloads = new DownloadService(_settings);
    }

    private string ResolvePlaybackUrl(Track track, bool remote, EmbyClient? client) =>
        PlaybackSourceResolver.GetUrl(track, _downloads?.GetLocalPath(track), remote, client);

    private async Task LoadHomeAsync()
    {
        if (_emby is null) return;
        var recent = _emby.GetAlbumsAsync(limit: 12);
        var favorites = _emby.GetAlbumsAsync(favoritesOnly: true, limit: 8);
        await Task.WhenAll(recent, favorites);
        Replace(RecentAlbums, recent.Result); Replace(FavoriteAlbums, favorites.Result);
        Notify(nameof(HasFavoriteAlbums));
        ApplyHomeModules();
        _ = RefreshListeningStatsAsync();
    }

    private async void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string section }) return;
        _searchTimer.Stop(); _search = ""; SearchBox.Text = ""; _albumId = ""; _page = 0;
        ShowPage(section);
        await GuardAsync(() => LoadSectionAsync(section));
    }

    private void ShowPage(string section)
    {
        _pageCts?.Cancel();
        if (_section != "lyrics" && section == "lyrics")
        {
            _previousSection = _section;
            _lyricsReturnHeading = (PageTitle, PageSubtitle);
        }
        else if (_section == "lyrics" && section == _previousSection && _lyricsReturnHeading is { } heading)
        {
            PageTitle = heading.Title; PageSubtitle = heading.Subtitle;
        }
        // Keep the collection while showing its player/queue, so returning preserves paging.
        if (section is "home" or "tracks" or "favorites" or "album" or "albums" or "library" or "browse") _collectionTracks = null;
        _section = section;
        foreach (var view in new FrameworkElement[] { HomePage, TracksPage, AlbumsPage, QueuePage, LyricsPage, DevicesPage, SettingsPage, LibraryOverviewPage, LibraryBrowsePage }) view.Visibility = Visibility.Collapsed;
        ImmersivePlayer.Visibility = section == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        switch (section)
        {
            case "home": HomePage.Visibility = Visibility.Visible; PageTitle = "为你推荐"; PageSubtitle = "让每一次打开，都有好音乐相伴。"; break;
            case "tracks": case "favorites": case "album": case "collection": TracksPage.Visibility = Visibility.Visible; PageTitle = section == "favorites" ? "我喜欢的" : section is "album" or "collection" ? PageTitle : "全部歌曲"; PageSubtitle = section == "favorites" ? "你喜欢的每一首，都在这里。" : section is "album" or "collection" ? PageSubtitle : "收藏的声音，随时在身边。"; break;
            case "library": LibraryOverviewPage.Visibility = Visibility.Visible; PageTitle = "媒体库"; PageSubtitle = "按专辑、艺术家、分类或服务器文件夹浏览"; break;
            case "browse": LibraryBrowsePage.Visibility = Visibility.Visible; break;
            case "albums": AlbumsPage.Visibility = Visibility.Visible; PageTitle = "专辑"; PageSubtitle = $"{_albumCount:N0} 张专辑 · 按最近添加排序"; break;
            case "queue": QueuePage.Visibility = Visibility.Visible; PageTitle = "播放队列"; PageSubtitle = $"{Queue.Count:N0} 首歌曲 · {QueueScopeLabel} · 双击立即播放"; break;
            case "lyrics": LyricsPage.Visibility = Visibility.Visible; PageTitle = Current?.Title ?? "歌词"; PageSubtitle = Current?.Artist ?? "开始播放后自动匹配歌词"; break;
            case "devices": DevicesPage.Visibility = Visibility.Visible; PageTitle = "音频与投放"; PageSubtitle = "在电脑上聆听，或让音乐充满整个房间。"; break;
            case "settings": SettingsPage.Visibility = Visibility.Visible; PageTitle = "设置"; PageSubtitle = "连接你的媒体库，调整自己的聆听方式。"; break;
        }
        Motion.Enter(PageTransitionHost, _appearance, page: true);
        Motion.Enter(PageHeading, _appearance, page: true);
        if (section == "lyrics") AnimatedLyrics.UpdatePlayback(_position, _playing, _settings.LyricsOffsetMs);
        SyncSpectrumCapture();
    }

    private Task LoadSectionAsync(string section) => section switch
    {
        "home" => LoadHomeAsync(),
        "tracks" or "favorites" or "album" or "collection" => LoadTracksAsync(),
        "albums" => LoadAlbumsAsync(false),
        _ => Task.CompletedTask
    };

    private async Task LoadTracksAsync()
    {
        if (_section == "collection" && _collectionTracks is not null) { await RefreshCollectionPage(); return; }
        if (_emby is null) { Status = "请先在设置中连接 Emby。"; return; }
        _pageCts?.Cancel(); _pageCts?.Dispose(); _pageCts = new();
        var ct = _pageCts.Token;
        Status = "正在读取歌曲…";
        var result = await _emby.GetTracksAsync(_search, _page * 200, 200, string.IsNullOrEmpty(_albumId) ? null : _albumId, _section == "favorites", ct);
        if (ct.IsCancellationRequested) return;
        Replace(Tracks, result.Items); _resultCount = result.TotalCount;
        PreviousPageButton.IsEnabled = _page > 0; NextPageButton.IsEnabled = (_page + 1) * 200 < _resultCount;
        Notify(nameof(PagingLabel));
        Status = result.TotalCount == 0 ? "没有找到匹配的歌曲。" : $"共 {result.TotalCount:N0} 首 · 双击播放，右键更多操作";
    }

    private async Task LoadAlbumsAsync(bool append)
    {
        if (_emby is null) return;
        _pageCts?.Cancel(); _pageCts?.Dispose(); _pageCts = new();
        var ct = _pageCts.Token;
        if (!append) _albumStart = 0;
        var result = await _emby.GetAlbumsAsync(limit: 48, search: _search, start: _albumStart, cancellationToken: ct);
        if (ct.IsCancellationRequested) return;
        if (!append) Albums.Clear();
        foreach (var album in result) Albums.Add(album);
        _albumStart += result.Count;
        MoreAlbumsButton.Visibility = result.Count == 48 ? Visibility.Visible : Visibility.Collapsed;
        Status = $"已显示 {Albums.Count:N0} 张专辑 · 点击查看曲目";
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null) SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (!_ready) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task ApplySearchAsync()
    {
        var text = SearchBox.Text.Trim();
        if (_section == "settings")
        {
            foreach (var card in SettingsCards.Children.OfType<Border>())
                card.Visibility = text.Length == 0 || (card.Tag as string ?? "").Contains(text, StringComparison.CurrentCultureIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (text == _search) return;
        _search = text; _page = 0; _albumId = "";
        if (_section != "albums") ShowPage("tracks");
        PageTitle = string.IsNullOrEmpty(text) ? (_section == "albums" ? "专辑" : "全部歌曲") : $"搜索 · {text}";
        PageSubtitle = "在整个 Emby 音乐库中查找";
        await GuardAsync(() => _section == "albums" ? LoadAlbumsAsync(false) : LoadTracksAsync());
    }

    private async void Album_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Album album }) return;
        _albumId = album.Id; _page = 0; _search = ""; SearchBox.Text = "";
        PageTitle = album.Title; PageSubtitle = album.Artist + (album.Year is { } year ? $" · {year}" : "");
        ShowPage("album");
        await GuardAsync(LoadTracksAsync);
    }

    private async void PreviousPage_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; await GuardAsync(LoadTracksAsync); } }
    private async void NextPage_Click(object sender, RoutedEventArgs e) { if ((_page + 1) * 200 < _resultCount) { _page++; await GuardAsync(LoadTracksAsync); } }
    private async void MoreAlbums_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => LoadAlbumsAsync(true));
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_section == "collection" && _collectionTracks is not null) { await RefreshCollectionPage(); return; }
        if (_emby is null) await ConnectAsync();
        else { _emby.InvalidateLibraryCache(); await LoadSectionAsync(_section); }
    });

    private async Task<List<Track>> GetSelectionAsync(bool wholeLibrary = false, bool favorites = false)
    {
        if (!wholeLibrary && _section == "collection" && _collectionTracks is not null) return _collectionTracks.ToList();
        if (_closing) throw new OperationCanceledException();
        if (_emby is null) throw new InvalidOperationException("请先连接 Emby。");
        _queueSelectionCts?.Cancel();
        _queueSelectionCts?.Dispose();
        _queueSelectionCts = new CancellationTokenSource();
        var cancellationToken = _queueSelectionCts.Token;
        var client = _emby;
        var connectVersion = _connectVersion;
        var search = wholeLibrary ? null : _search;
        var albumId = wholeLibrary || string.IsNullOrEmpty(_albumId) ? null : _albumId;
        var favoritesOnly = favorites || (!wholeLibrary && _section == "favorites");
        Status = "正在准备播放队列…";
        var list = new List<Track>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0;
        var count = int.MaxValue;
        do
        {
            var result = await client.GetTracksAsync(search, offset, 1000, albumId, favoritesOnly, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_closing || client != _emby || connectVersion != _connectVersion) throw new OperationCanceledException();
            if (offset == 0) count = result.TotalCount;
            offset += result.Items.Count;
            list.AddRange(result.Items.Where(track => ids.Add(track.Id)));
            Status = $"正在准备完整队列 · {list.Count:N0} / {count:N0} 首";
            if (result.Items.Count == 0) break;
        } while (offset < count);
        return list;
    }

    private async Task StartQueueAsync(IEnumerable<Track> tracks, bool shuffle = false, Track? first = null)
    {
        if (_closing) return;
        _queueSelectionCts?.Cancel();
        var fromCurrentPage = ReferenceEquals(tracks, Tracks);
        var list = tracks.ToList();
        if (list.Count == 0) { Status = "这里还没有可以播放的歌曲。"; return; }
        if (shuffle) Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
        _queueScopeLabel = fromCurrentPage ? $"当前第 {_page + 1} 页队列" : "完整选择队列";
        _queueSourceCount = list.Count;
        Replace(Queue, list);
        Notify(nameof(QueueScopeLabel));
        _queueIndex = first is null ? 0 : Math.Max(0, list.FindIndex(t => t.Id == first.Id));
        if (_section == "queue") PageSubtitle = $"{Queue.Count:N0} 首歌曲 · {QueueScopeLabel} · 双击立即播放";
        await PlayTrackAsync(Queue[_queueIndex]);
    }
    private async void PlayList_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => await StartQueueAsync(await GetSelectionAsync()));
    private async void ShuffleList_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => await StartQueueAsync(await GetSelectionAsync(), true));
    private async void ShuffleLibrary_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => await StartQueueAsync(await GetSelectionAsync(true), true));
    private async void FavoriteMix_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => await StartQueueAsync(await GetSelectionAsync(true, true), true));
    private async void RandomAlbum_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_emby is null) return;
        var albums = await _emby.GetAlbumsAsync(limit: 1, start: Random.Shared.Next(Math.Max(1, _albumCount)));
        if (albums.FirstOrDefault() is not { } album) return;
        var tracks = await _emby.GetTracksAsync(albumId: album.Id, limit: 1000);
        await StartQueueAsync(tracks.Items);
    });
    private async void QueueList_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => { foreach (var track in await GetSelectionAsync()) Queue.Add(track); Status = $"播放队列现有 {Queue.Count:N0} 首歌曲"; });
    private void AddQueue_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: Track track }) { Queue.Add(track); Status = $"已加入队列 · {track.Title}"; } e.Handled = true; }
    private async void Track_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is Track track) await GuardAsync(() => StartQueueAsync(Tracks, first: track));
    }
    private async void Queue_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is Track track) { _queueIndex = Queue.IndexOf(track); await GuardAsync(() => PlayTrackAsync(track)); }
    }
    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null) { if (obj is T match) return match; obj = obj is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(obj) : LogicalTreeHelper.GetParent(obj); }
        return null;
    }
    private async void ContextPlay_Click(object sender, RoutedEventArgs e) { if (TrackList.SelectedItem is Track track) await GuardAsync(() => StartQueueAsync(Tracks, first: track)); }
    private void ContextNext_Click(object sender, RoutedEventArgs e) { if (TrackList.SelectedItem is Track track) { Queue.Insert(Math.Clamp(_queueIndex + 1, 0, Queue.Count), track); Status = $"下一首播放 · {track.Title}"; } }
    private async void ContextFavorite_Click(object sender, RoutedEventArgs e) { if (TrackList.SelectedItem is Track track) await ToggleFavoriteAsync(track); }
    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        Queue.Clear(); _queueIndex = -1;
        if (Current is not null) { Queue.Add(Current); _queueIndex = 0; }
        PageSubtitle = $"{Queue.Count} 首歌曲 · 当前播放已保留";
    }
    private void RemoveQueue_Click(object sender, RoutedEventArgs e)
    {
        if (QueueList.SelectedIndex < 0) return;
        var index = QueueList.SelectedIndex;
        if (index == _queueIndex) { Status = "正在播放的歌曲会保留，可移除其他待播歌曲。"; return; }
        Queue.RemoveAt(index);
        if (index < _queueIndex) _queueIndex--;
        PageSubtitle = $"{Queue.Count:N0} 首歌曲 · 双击立即播放";
    }

    private async Task PlayTrackAsync(Track track)
    {
        if (_closing) return;
        var client = _emby;
        var connectVersion = _connectVersion;
        var version = ++_playVersion;
        _queueSelectionCts?.Cancel();
        _lyricCts?.Cancel();
        await _transportGate.WaitAsync();
        try
        {
            if (_closing || version != _playVersion || client != _emby || connectVersion != _connectVersion) return;
            var url = ResolvePlaybackUrl(track, _renderer is not null, client);
            var old = Current;
            var oldPosition = _position;
            if (old is not null) _history?.EndTrack(old, oldPosition);
            _playing = false; _position = 0; _duration = track.DurationMs;
            Current = track; _lyricIndex = -2; _document = LyricsDocument.Empty("正在寻找歌词…");
            _history?.BeginTrack(track);
            var queueIndex = Queue.IndexOf(track);
            if (queueIndex >= 0) _queueIndex = queueIndex;
            UpdateLyricsBindings();
            Status = $"正在播放 · {track.Title} · {QueueScopeLabel} {Queue.Count:N0} 首";
            if (old is not null && client is not null) _ = QueueSessionReportAsync(client, old, oldPosition, true, "stop");
            if (_renderer is { } renderer)
            {
                _player?.Stop();
                await _dlna.PlayAsync(renderer, url, track);
                if (_closing || version != _playVersion || client != _emby || connectVersion != _connectVersion) return;
                _remoteStarted = DateTime.UtcNow; _remoteHasPlayed = false;
            }
            else
            {
                if (_player is null) throw new InvalidOperationException("本地音频引擎未能初始化，请重启应用。");
                _player.Play(url);
            }
            _playing = true; NotifyNowPlaying();
            _desktop?.SetTrack(track.Title, track.Artist);
            if (client is not null) _ = QueueSessionReportAsync(client, track, 0, false, "start");
            _ = LoadLyricsAsync(track, version);
            StartAutomaticCache(track);
        }
        catch
        {
            if (_closing || version != _playVersion || client != _emby || connectVersion != _connectVersion) return;
            _playing = false; NotifyNowPlaying();
            throw;
        }
        finally { _transportGate.Release(); }
    }

    // Statistics must remain ordered without holding up audible transport operations.
    // The client is captured so a reconnect never reports old track IDs to a new server.
    private Task QueueSessionReportAsync(EmbyClient client, Track track, long position, bool paused, string kind)
    {
        if (PlaybackSourceResolver.IsRadio(track)) return Task.CompletedTask;
        var previous = _sessionReports;
        return _sessionReports = SendAfterPreviousAsync();
        async Task SendAfterPreviousAsync()
        {
            try
            {
                await previous;
                if (client != _emby) return;
                if (_closing && kind != "stop") return;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ReportPlaybackAsync(track, position, paused, kind, timeout.Token);
            }
            catch { /* Session reporting is best effort; never interrupt audio playback. */ }
        }
    }

    private async Task LoadLyricsAsync(Track track, int version)
    {
        _lyricCts?.Cancel(); _lyricCts?.Dispose(); _lyricCts = new();
        if (PlaybackSourceResolver.IsRadio(track))
        {
            _document = LyricsDocument.Empty("网络电台未提供同步歌词。");
            UpdateLyricsBindings();
            return;
        }
        var ct = _lyricCts.Token;
        try
        {
            string? server = null;
            if (_emby is not null)
            {
                using var serverTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                serverTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                try { server = await _emby.GetServerLyricsAsync(track, serverTimeout.Token); }
                catch (Exception) when (!ct.IsCancellationRequested) { /* Public sources can still work when Emby lyrics fail. */ }
            }
            ct.ThrowIfCancellationRequested();
            var document = await _lyrics.GetLyricsAsync(track, server, ct);
            if (version != _playVersion || ct.IsCancellationRequested || _closing) return;
            _document = document; _lyricIndex = -2; UpdateLyricsBindings();
            Status = document.IsSynced ? $"同步歌词已就绪 · {document.Source}" : document.IsInstrumental ? "纯音乐，请欣赏。" : "暂无匹配的同步歌词，可在歌词页导入 LRC。";
        }
        catch (OperationCanceledException) { }
        catch { if (version == _playVersion) { _document = LyricsDocument.Empty("暂未找到歌词"); UpdateLyricsBindings(); } }
    }

    private void UpdateLyricsBindings()
    {
        Replace(LyricLines, _document.Lines);
        AnimatedLyrics.SetDocument(_document);
        AnimatedLyrics.UpdatePlayback(_position, _playing, _settings.LyricsOffsetMs);
        foreach (var name in new[] { nameof(HasPlainLyrics), nameof(PlainLyrics), nameof(LyricsInfo), nameof(CurrentLyric), nameof(NextLyric) }) Notify(name);
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (_closing || _switchingOutput) return;
        _tick++;
        if (_renderer is null && _player is not null)
        {
            _position = Math.Max(0, _player.PositionMs);
            if (_player.DurationMs > 0) _duration = _player.DurationMs;
        }
        else if (_renderer is { } device && _tick % 5 == 0 && !_polling && Current is not null && _transportGate.CurrentCount > 0)
        {
            _polling = true;
            var version = _playVersion;
            var outputVersion = _outputSwitchVersion;
            try
            {
                var info = await _dlna.GetPositionAsync(device);
                if (_closing || _switchingOutput || _transportGate.CurrentCount == 0 || _renderer != device || version != _playVersion || outputVersion != _outputSwitchVersion) return;
                var lastPosition = Math.Max(_position, info.PositionMs);
                _position = Math.Max(0, info.PositionMs);
                if (info.DurationMs > 0) _duration = info.DurationMs;
                if (info.State.Equals("PLAYING", StringComparison.OrdinalIgnoreCase)) { _remoteHasPlayed = true; _playing = true; }
                else if (info.State.Contains("PAUSED", StringComparison.OrdinalIgnoreCase)) _playing = false;
                else if (info.State.Equals("STOPPED", StringComparison.OrdinalIgnoreCase) || info.State.Equals("NO_MEDIA_PRESENT", StringComparison.OrdinalIgnoreCase))
                {
                    var elapsed = DateTime.UtcNow - _remoteStarted;
                    // STOPPED is also returned after an external stop or a decode failure.
                    // Advance only when a previously playing track actually reached its end.
                    var reachedEnd = _duration > 0 && lastPosition >= Math.Max(0, _duration - 3000);
                    if (_playing && _remoteHasPlayed && reachedEnd)
                    {
                        _playing = false; _remoteHasPlayed = false; _position = _duration;
                        await AdvanceAsync(true);
                    }
                    else if (_remoteHasPlayed || elapsed > TimeSpan.FromSeconds(8))
                    {
                        var wasPlaying = _playing;
                        _playing = false;
                        _position = lastPosition;
                        if (wasPlaying) Status = _remoteHasPlayed ? "音箱已停止播放，可点击播放继续。" : "音箱尚未开始播放，请检查其支持的音频格式或重新投放。";
                    }
                }
                Notify(nameof(PlayGlyph));
            }
            catch
            {
                if (!_closing && !_switchingOutput && _renderer == device && version == _playVersion && outputVersion == _outputSwitchVersion)
                    Status = "暂时无法读取音箱状态，请检查网络或重新投放。";
            }
            finally { _polling = false; }
        }
        if (Current is { } listening)
            _history?.UpdatePlayback(listening, _position, _playing && (_renderer is not null ? _remoteHasPlayed : _player?.IsPlaying == true));
        ImmersivePlayer.UpdatePlayback(_position, _playing);
        SyncSpectrumCapture();
        if (!_seeking) SeekBar.Value = _duration > 0 ? Math.Clamp(_position * 100.0 / _duration, 0, 100) : 0;
        Notify(nameof(PositionLabel)); Notify(nameof(DurationLabel));
        var line = LyricsService.FindLineIndex(_document, _position, _settings.LyricsOffsetMs);
        if (line != _lyricIndex)
        {
            _lyricIndex = line;
            Notify(nameof(CurrentLyric)); Notify(nameof(NextLyric));
            Motion.Enter(SidebarCurrentLyric, _appearance);
            Motion.Enter(SidebarNextLyric, _appearance);
        }
        if (_section == "lyrics") AnimatedLyrics.UpdatePlayback(_position, _playing, _settings.LyricsOffsetMs);
        if (_desktop?.IsVisible == true) _desktop.UpdateLyrics(_document, _position, _playing);
        if (_tick % 50 == 0 && Current is { } current && !_reporting)
        {
            _reporting = true;
            try { await ReportAsync(current, _position, !_playing, "progress"); }
            finally { _reporting = false; }
        }
    }

    private async Task AdvanceAsync(bool automatic)
    {
        if (_closing) return;
        if (automatic)
        {
            // Ignore duplicate/stale native end events while a new track or output is opening.
            if (_switchingOutput || _transportGate.CurrentCount == 0 || _automaticAdvanceVersion == _playVersion || (_renderer is null && _player?.HasEnded != true)) return;
            _automaticAdvanceVersion = _playVersion;
        }
        if (Queue.Count == 0) { _playing = false; NotifyNowPlaying(); return; }
        if (automatic && _repeat == 2 && Current is not null) { await GuardAsync(() => PlayTrackAsync(Current)); return; }
        int index;
        if (_shuffle && Queue.Count > 1)
        {
            index = Random.Shared.Next(Queue.Count - 1);
            if (index >= _queueIndex) index++;
        }
        else index = _queueIndex + 1;
        if (index >= Queue.Count)
        {
            if (_repeat == 1 || !automatic) index = 0;
            else
            {
                if (Current is { } finished) _history?.EndTrack(finished, _position);
                _playing = false; NotifyNowPlaying(); Status = "本次队列播放完毕。";
                if (Current is { } track && _emby is { } client) _ = QueueSessionReportAsync(client, track, _position, true, "stop");
                return;
            }
        }
        _queueIndex = Math.Clamp(index, 0, Queue.Count - 1);
        await GuardAsync(() => PlayTrackAsync(Queue[_queueIndex]));
    }
    private async void Next_Click(object sender, RoutedEventArgs e) => await AdvanceAsync(false);
    private async void Previous_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_position > 3000) { await SeekAsync(0); return; }
        if (Queue.Count == 0) return;
        _queueIndex = (_queueIndex - 1 + Queue.Count) % Queue.Count;
        await PlayTrackAsync(Queue[_queueIndex]);
    });
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await GuardAsync(TogglePlayAsync);
    private async Task TogglePlayAsync()
    {
        if (_closing) return;
        if (Current is null)
        {
            if (Queue.Count > 0) { _queueIndex = 0; await PlayTrackAsync(Queue[0]); }
            else if (Tracks.Count > 0) await StartQueueAsync(Tracks);
            else await StartQueueAsync(await GetSelectionAsync(true), true);
            return;
        }
        var version = _playVersion;
        var client = _emby;
        Track? restart = null;
        await _transportGate.WaitAsync();
        try
        {
            if (_closing || version != _playVersion || client != _emby || Current is not { } track) return;
            if (!_playing && ((_duration > 0 && _position >= _duration - 500) || (_renderer is null && _player?.IsPaused != true)))
            {
                restart = track;
            }
            else
            {
                var play = !_playing;
                var renderer = _renderer;
                if (renderer is not null)
                {
                    if (play) await _dlna.ResumeAsync(renderer); else await _dlna.PauseAsync(renderer);
                }
                else
                {
                    if (_player is null) throw new InvalidOperationException("本地音频引擎未能初始化，请重启应用。");
                    if (play) _player.Resume(); else _player.Pause();
                }
                if (_closing || version != _playVersion || client != _emby || renderer != _renderer) return;
                _playing = play; NotifyNowPlaying();
                if (client is not null) _ = QueueSessionReportAsync(client, track, _position, !play, "progress");
            }
        }
        finally { _transportGate.Release(); }
        if (restart is not null) await PlayTrackAsync(restart);
    }
    private void ShuffleToggle_Click(object sender, RoutedEventArgs e) { _shuffle = !_shuffle; Notify(nameof(ShuffleBrush)); Status = _shuffle ? "已开启随机播放" : "已关闭随机播放"; }
    private void Repeat_Click(object sender, RoutedEventArgs e) { _repeat = (_repeat + 1) % 3; Notify(nameof(RepeatGlyph)); Notify(nameof(RepeatBrush)); Notify(nameof(RepeatLabel)); Status = RepeatLabel; }
    private void Seek_Down(object sender, MouseButtonEventArgs e) => _seeking = true;
    private bool _seekReleasePending;
    private async void Seek_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking || _seekReleasePending) return;
        _seekReleasePending = true;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            var target = (long)(_duration * SeekBar.Value / 100);
            _seeking = false;
            await GuardAsync(() => SeekAsync(target));
        }
        finally { _seekReleasePending = false; }
    }
    private async void Seek_KeyUp(object sender, KeyEventArgs e) { if (e.Key is Key.Left or Key.Right or Key.Home or Key.End) { await GuardAsync(() => SeekAsync((long)(_duration * SeekBar.Value / 100))); e.Handled = true; } }
    private async Task SeekAsync(long position)
    {
        if (Current is not { } track || PlaybackSourceResolver.IsRadio(track)) return;
        var version = _playVersion;
        var renderer = _renderer;
        await _transportGate.WaitAsync();
        try
        {
            if (_closing || version != _playVersion || Current != track || renderer != _renderer) return;
            position = Math.Clamp(position, 0, Math.Max(0, _duration));
            if (renderer is not null) await _dlna.SeekAsync(renderer, position); else _player?.Seek(position);
            if (_closing || version != _playVersion || Current != track || renderer != _renderer) return;
            _position = position;
            AnimatedLyrics.UpdatePlayback(_position, _playing, _settings.LyricsOffsetMs, forceRebase: true);
            _desktop?.UpdateLyrics(_document, _position, _playing, forceRebase: true);
            SeekBar.Value = _duration > 0 ? _position * 100d / _duration : 0;
            Notify(nameof(PositionLabel));
            _ = ReportAsync(track, _position, !_playing, "progress");
        }
        finally { _transportGate.Release(); }
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        _settings.Volume = (int)e.NewValue;
        if (_player is not null) _player.Volume = _settings.Volume;
        Notify(nameof(VolumeLabel));
        _volumeTimer.Stop(); _volumeTimer.Start();
    }
    private async void Favorite_Click(object sender, RoutedEventArgs e) { if (Current is { } track) await ToggleFavoriteAsync(track); }
    private async Task ToggleFavoriteAsync(Track track) => await GuardAsync(async () =>
    {
        if (_emby is null || _closing) return;
        if (PlaybackSourceResolver.IsRadio(track)) { Status = "网络电台保存在本机，不支持 Emby 收藏。"; return; }
        var client = _emby;
        var connectVersion = _connectVersion;
        var refreshFavorites = false;
        await _favoriteGate.WaitAsync();
        try
        {
            if (_closing || client != _emby || connectVersion != _connectVersion) return;
            var latest = Current?.Id == track.Id ? Current : Tracks.Concat(Queue).FirstOrDefault(t => t.Id == track.Id) ?? track;
            var favorite = !latest.IsFavorite;
            await client.SetFavoriteAsync(track, favorite);
            if (_closing || client != _emby || connectVersion != _connectVersion) return;
            foreach (var other in Tracks.Concat(Queue).Where(t => t.Id == track.Id)) other.IsFavorite = favorite;
            if (Current?.Id == track.Id) Current.IsFavorite = favorite;
            Notify(nameof(FavoriteGlyph));
            Status = favorite ? "已加入 Emby 收藏" : "已取消 Emby 收藏";
            refreshFavorites = _section == "favorites" && !favorite;
        }
        finally { _favoriteGate.Release(); }
        if (refreshFavorites && _section == "favorites" && !_closing) await LoadTracksAsync();
    });

    private void RefreshAudioDevices()
    {
        if (_player is null) return;
        Replace(AudioDevices, _player.GetAudioDevices());
        if (!AudioDevices.Any(d => d.Id == _settings.AudioDeviceId)) _settings.AudioDeviceId = "";
        _player.SetAudioDevice(_settings.AudioDeviceId);
        AudioDeviceBox.SelectedValue = _settings.AudioDeviceId;
        Notify(nameof(OutputLabel));
    }
    private void RefreshDevices_Click(object sender, RoutedEventArgs e) { try { RefreshAudioDevices(); Status = $"发现 {Math.Max(0, AudioDevices.Count - 1)} 个音频设备"; } catch (Exception ex) { Status = SafeMessage(ex); } }
    private void AudioDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || AudioDeviceBox.SelectedItem is not AudioDevice device) return;
        try { _player?.SetAudioDevice(device.Id); _settings.AudioDeviceId = device.Id; Notify(nameof(OutputLabel)); SaveSettings(); SyncSpectrumCapture(); Status = $"本机输出 · {device.Name}"; }
        catch (Exception ex) { Status = SafeMessage(ex); }
    }
    private void Output_Click(object sender, RoutedEventArgs e)
    {
        PopupAudioDeviceBox.SelectedValue = _settings.AudioDeviceId;
        OutputPopup.IsOpen = !OutputPopup.IsOpen;
        if (OutputPopup.IsOpen && DlnaDevices.Count == 0) _ = DiscoverPopupDevicesAsync();
    }
    private async void Discover_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        DiscoverButton.IsEnabled = false; DlnaStatus = "正在搜索局域网音箱…";
        try
        {
            Replace(DlnaDevices, await _dlna.DiscoverAsync());
            if (DlnaDevices.Count > 0) DlnaList.SelectedIndex = 0;
            DlnaStatus = DlnaDevices.Count == 0 ? "未发现设备。请确认音箱已开启 DLNA，与电脑在同一局域网，并允许应用通过 Windows 防火墙。" : $"找到 {DlnaDevices.Count} 个设备，选择后即可投放。";
        }
        finally { DiscoverButton.IsEnabled = true; }
    });
    private async void Cast_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (Current is null) { Status = "请先选择一首歌曲。"; return; }
        if (DlnaList.SelectedItem is not DlnaDevice device) { Status = "请先搜索并选择投放设备。"; return; }
        await SwitchOutputAsync(device);
    });
    private async void UseLocal_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => SwitchOutputAsync(null));
    private async Task SwitchOutputAsync(DlnaDevice? target)
    {
        var request = ++_outputSwitchVersion;
        int? mutedLocalVolume = null;
        await _transportGate.WaitAsync();
        try
        {
            if (_closing || request != _outputSwitchVersion || _renderer == target) return;
            if (target is null && _player is null) throw new InvalidOperationException("本地音频引擎未能初始化，请重启应用。");
            _switchingOutput = true;
            var old = _renderer;
            var track = Current;
            var version = _playVersion;
            // Resolve before stopping the current output, so an unavailable source does not interrupt it.
            var url = track is null ? null : ResolvePlaybackUrl(track, target is not null, _emby);
            var position = old is null ? Math.Max(0, _player?.PositionMs ?? _position) : _position;
            var wasPlaying = _playing;
            if (track is not null) _history?.UpdatePlayback(track, position, false);
            if (_outputHandoff is { } prior && prior.Track == track && prior.Version == version)
            {
                position = prior.Position;
                wasPlaying = prior.Playing;
            }
            _outputHandoff = (track, version, position, wasPlaying);
            var previousStopFailed = false;
            var seekFailed = false;
            Status = target is null ? "正在切回此电脑…" : $"正在连接 {target.Name}…";
            if (old is not null)
            {
                // A speaker that has gone offline must not prevent recovery to local audio.
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _dlna.StopAsync(old, stopTimeout.Token); }
                catch { previousStopFailed = true; }
            }
            else _player?.Stop();
            _playing = false;
            _renderer = target;
            _remoteHasPlayed = false;
            _remoteStarted = DateTime.UtcNow;
            NotifyNowPlaying();
            if (track is null || _closing || request != _outputSwitchVersion || version != _playVersion) return;

            // Keep the same track, lyric document and Emby session during output handoff.
            // PlayTrackAsync also acquires this gate and must not be called recursively here.
            if (target is not null) await _dlna.PlayAsync(target, url!, track);
            else
            {
                // libVLC prepares HTTP input asynchronously. Muting a paused handoff avoids
                // a short audible burst before the new input starts accepting pause/seek.
                if (!wasPlaying) { mutedLocalVolume = _player!.Volume; _player.Volume = 0; }
                _player!.Play(url!);
                for (var attempt = 0; attempt < 15 && !_player.IsPlaying && _player.DurationMs <= 0; attempt++)
                {
                    await Task.Delay(100);
                    if (_closing || request != _outputSwitchVersion || version != _playVersion) return;
                }
            }
            _playing = true;
            _remoteStarted = DateTime.UtcNow;
            if (_closing || request != _outputSwitchVersion || version != _playVersion) return;

            if (!wasPlaying)
            {
                if (target is not null) await _dlna.PauseAsync(target);
                else _player!.Pause();
                _playing = false;
            }
            if (position > 0)
            {
                // Give the new renderer a short preparation window before seeking. A newer
                // user action invalidates this seek, preventing it from hitting another song.
                await Task.Delay(600);
                if (_closing || request != _outputSwitchVersion || version != _playVersion) return;
                try
                {
                    if (target is not null) await _dlna.SeekAsync(target, position);
                    else _player!.Seek(position);
                    _position = position;
                }
                catch { seekFailed = true; _position = 0; }
            }
            if (_closing || request != _outputSwitchVersion || version != _playVersion) return;
            if (!wasPlaying && target is null) _player!.Pause();
            NotifyNowPlaying();
            Status = target is null ? "已切换到此电脑" : $"正在投放到 {target.Name}";
            if (seekFailed) Status += " · 设备未接受续播位置，将从头播放";
            if (previousStopFailed) Status += " · 旧音箱未响应停止，请检查其状态";
        }
        catch
        {
            _playing = false;
            Notify(nameof(PlayGlyph));
            throw;
        }
        finally
        {
            if (mutedLocalVolume is { } volume)
            {
                try { if (_player is not null) _player.Volume = volume; } catch (ObjectDisposedException) { }
            }
            _switchingOutput = false;
            if (request == _outputSwitchVersion) _outputHandoff = null;
            _transportGate.Release();
        }
    }

    private void DesktopLyrics_Click(object sender, RoutedEventArgs e) => SetDesktopLyrics(!_settings.DesktopLyricsEnabled);
    private void DesktopEnabled_Click(object sender, RoutedEventArgs e) => SetDesktopLyrics(DesktopEnabledBox.IsChecked == true);
    private void SetDesktopLyrics(bool enabled)
    {
        _settings.DesktopLyricsEnabled = enabled;
        if (enabled)
        {
            if (_desktop is null)
            {
                _desktop = new DesktopLyricsWindow { OffsetMs = _settings.LyricsOffsetMs, LyricsFontSize = _settings.LyricFontSize };
                _desktop.Left = Math.Clamp(_settings.LyricsLeft, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - _desktop.Width));
                _desktop.Top = Math.Clamp(_settings.LyricsTop, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - _desktop.Height));
                _desktop.UserClosed += () => SetDesktopLyrics(false);
                _desktop.OptionsChanged += DesktopOptionsChanged;
            }
            _desktop.SetTrack(CurrentTitle, CurrentArtist);
            _desktop.ApplyAppearance(_appearance);
            _desktop.Show(); _desktop.IsLocked = _settings.LyricsLocked;
            _desktop.UpdateLyrics(_document, _position, _playing);
        }
        else _desktop?.Hide();
        DesktopEnabledBox.IsChecked = enabled;
        Notify(nameof(DesktopLyricsLabel)); SaveSettings();
    }
    private void DesktopOptionsChanged()
    {
        if (_desktop is null || _appearancePreviewActive) return;
        _settings.LyricsOffsetMs = _desktop.OffsetMs; _settings.LyricFontSize = _desktop.LyricsFontSize;
        ImmersivePlayer.SetLyricsOffset(_settings.LyricsOffsetMs);
        AnimatedLyrics.UpdatePlayback(_position, _playing, _settings.LyricsOffsetMs);
        _settings.Appearance.DesktopFontSize = _desktop.LyricsFontSize;
        _settings.Appearance.DesktopWidth = _desktop.Width;
        _appearance.DesktopFontSize = _desktop.LyricsFontSize;
        _appearance.DesktopWidth = _desktop.Width;
        _settings.LyricsLocked = _desktop.IsLocked; _settings.LyricsLeft = _desktop.Left; _settings.LyricsTop = _desktop.Top;
        DesktopLockedBox.IsChecked = _settings.LyricsLocked;
        Notify(nameof(LyricsInfo)); SaveSettings();
    }
    private void DesktopLocked_Click(object sender, RoutedEventArgs e) { _settings.LyricsLocked = DesktopLockedBox.IsChecked == true; if (_desktop is not null) _desktop.IsLocked = _settings.LyricsLocked; SaveSettings(); }
    private void OffsetMinus_Click(object sender, RoutedEventArgs e) => SetOffset(_settings.LyricsOffsetMs - 250);
    private void OffsetPlus_Click(object sender, RoutedEventArgs e) => SetOffset(_settings.LyricsOffsetMs + 250);
    private void OffsetReset_Click(object sender, RoutedEventArgs e) => SetOffset(0);
    private void SetOffset(double offset)
    {
        _settings.LyricsOffsetMs = double.IsFinite(offset) ? Math.Clamp(offset, -600000, 600000) : 0;
        if (_desktop is not null) _desktop.OffsetMs = _settings.LyricsOffsetMs;
        ImmersivePlayer.SetLyricsOffset(_settings.LyricsOffsetMs);
        AnimatedLyrics.UpdatePlayback(_position, _playing, _settings.LyricsOffsetMs);
        Notify(nameof(LyricsInfo)); SaveSettings();
    }
    private void ApplyAppearance(LyricAppearanceSettings appearance)
    {
        _appearance = appearance.Clone();
        _appearance.Normalize();
        AnimatedLyrics.ApplyAppearance(_appearance);
        _desktop?.ApplyAppearance(_appearance);
        SidebarCurrentLyric.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_appearance.CurrentColor));
        SidebarNextLyric.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_appearance.InactiveColor));
        SidebarCurrentLyric.FontFamily = SidebarNextLyric.FontFamily = new FontFamily(_appearance.FontFamily);
        if (!_appearance.AnimationsEnabled)
        {
            Motion.Enter(PageTransitionHost, _appearance, true);
            Motion.Enter(PageHeading, _appearance, true);
            Motion.Enter(SidebarCurrentLyric, _appearance);
            Motion.Enter(SidebarNextLyric, _appearance);
        }
    }
    private void Appearance_Click(object sender, RoutedEventArgs e)
    {
        var original = _settings.Appearance.Clone();
        var editor = new LyricAppearanceWindow(original) { Owner = this };
        editor.PreviewChanged += ApplyAppearance;
        bool? accepted;
        _appearancePreviewActive = true;
        try { accepted = editor.ShowDialog(); }
        finally { _appearancePreviewActive = false; }
        if (accepted == true)
        {
            _settings.Appearance = editor.Result.Clone();
            _settings.LyricFontSize = _settings.Appearance.DesktopFontSize;
            ApplyAppearance(_settings.Appearance);
            SaveSettings();
            Status = "歌词样式与动效已保存。";
        }
        else ApplyAppearance(original);
    }
    private async void ImportLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (Current is null) { Status = "先播放一首歌曲，再导入对应的歌词。"; return; }
        var track = Current;
        var version = _playVersion;
        var picker = new OpenFileDialog { Filter = "歌词文件|*.lrc;*.yrc;*.qrc;*.ttml;*.xml;*.txt|所有文件|*.*", Title = "选择歌词" };
        if (picker.ShowDialog(this) == true) await GuardAsync(async () =>
        {
            if (version == _playVersion) _lyricCts?.Cancel();
            var document = await _lyrics.ImportLyricsAsync(track, await File.ReadAllTextAsync(picker.FileName));
            if (version != _playVersion || Current?.Id != track.Id || _closing) return;
            _document = document;
            _lyricIndex = -2; UpdateLyricsBindings(); Status = "已载入本地歌词";
        });
    }
    private async void Lyric_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is LyricsLine line)
            await GuardAsync(() => SeekAsync((long)(line.Time.TotalMilliseconds - _settings.LyricsOffsetMs)));
    }

    private AppSettings DraftSettings() => new() { ServerUrl = ServerBox.Text.Trim(), ApiKey = ApiKeyBox.Password.Trim(), UserId = (UsersBox.SelectedItem as EmbyUser)?.Id ?? "" };
    private async void TestConnection_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        Status = "正在测试服务器连接…";
        using var client = new EmbyClient(DraftSettings());
        var users = await client.GetUsersAsync();
        Replace(Users, users); UsersBox.SelectedIndex = users.Count > 0 ? 0 : -1;
        Status = $"连接成功，找到 {users.Count} 个用户。选择用户后保存即可。";
    });
    private async void SaveConnection_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        await StopForReconnectAsync();
        if (_closing) return;
        _settings.ServerUrl = ServerBox.Text.Trim(); _settings.ApiKey = ApiKeyBox.Password.Trim();
        _settings.UserId = (UsersBox.SelectedItem as EmbyUser)?.Id ?? "";
        _settings.UserName = (UsersBox.SelectedItem as EmbyUser)?.Name ?? "";
        _settings.LoginMode = "ApiKey";
        InitializeLocalStores();
        await ConnectAsync();
    });
    private void SaveSettings()
    {
        try { _saveSettings(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { Status = "设置未能保存，请检查用户目录权限。"; }
    }
    private Task ReportAsync(Track track, long position, bool paused, string kind) =>
        _emby is { } client ? QueueSessionReportAsync(client, track, position, paused, kind) : Task.CompletedTask;
    private async Task GuardAsync(Func<Task> action)
    {
        if (_closing) return;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = SafeMessage(ex); }
    }
    public static string SafeMessage(Exception ex)
    {
        var message = ex is HttpRequestException ? "网络请求失败，请检查服务器地址、局域网连接和设备状态。" : ex.Message;
        message = Regex.Replace(message, @"https?://[^\s'""<>]+", "[网络地址]", RegexOptions.IgnoreCase);
        message = Regex.Replace(message, @"(?i)(api_key|x-emby-token|token|apikey)\s*[=:]\s*\S+", "$1=[已隐藏]");
        return message.Length > 220 ? message[..220] : message;
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBoxBase or PasswordBox or ComboBox) return;
        if (e.Key is Key.Space or Key.MediaPlayPause) { e.Handled = true; await GuardAsync(TogglePlayAsync); }
        else if (e.Key == Key.MediaNextTrack) { e.Handled = true; await AdvanceAsync(false); }
        else if (e.Key == Key.MediaPreviousTrack) { e.Handled = true; Previous_Click(sender, e); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        if (!_exitRequested && _settings.CloseToTray && _tray?.Available == true)
        {
            Hide();
            Status = "已隐藏到托盘，音乐继续播放。右键托盘图标可退出。";
            return;
        }
        _closing = true;
        ++_playVersion;
        ++_connectVersion;
        _timer.Stop(); _searchTimer.Stop(); _volumeTimer.Stop();
        _pageCts?.Cancel(); _lyricCts?.Cancel();
        _queueSelectionCts?.Cancel();
        _downloadCts?.Cancel();
        await _downloadTask;
        SaveSettings();
        _desktop?.Hide();
        if (Current is { } track)
        {
            _history?.EndTrack(track, _position);
            var stop = ReportAsync(track, _position, true, "stop");
            await Task.WhenAny(stop, Task.Delay(1500));
        }
        var acquired = await _transportGate.WaitAsync(TimeSpan.FromSeconds(3));
        if (acquired)
        {
            try
            {
                if (_renderer is not null)
                {
                    try { await _dlna.StopAsync(_renderer).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
                }
                _player?.Dispose(); _emby?.Dispose(); _dlna.Dispose();
            }
            finally { _transportGate.Release(); }
        }
        _spectrumService.Dispose();
        _downloads?.Dispose();
        _theme.Dispose();
        _tray?.Dispose();
        _allowClose = true;
        Application.Current.Shutdown();
    }
}
