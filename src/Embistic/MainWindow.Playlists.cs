using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;

namespace Embistic;

public partial class MainWindow
{
    private LocalPlaylistService? _localPlaylists;
    private readonly Dictionary<string, LocalPlaylistService> _playlistStores = new(StringComparer.OrdinalIgnoreCase);
    private bool _queueDetached = true, _replacingQueue;
    private int _queueRevision, _albumQueueVersion;
    private string _queueAlbumId = "";
    private CancellationTokenSource? _albumQueueCts;
    private Task _playlistWrites = Task.CompletedTask;
    public string QueueSummaryLabel => $"{Queue.Count:N0} 首 · {QueueScopeLabel}";

    private void InitializeQueueControls()
    {
        QueuePopupContent.SetQueue(Queue);
        Queue.CollectionChanged += Queue_CollectionChanged;
        QueuePopupContent.CloseRequested += () => QueuePopup.IsOpen = false;
        QueuePopupContent.ClearRequested += () => ClearQueue_Click(this, new RoutedEventArgs());
        QueuePopupContent.RemoveRequested += RemoveQueueAt;
        QueuePopupContent.MoveRequested += MoveQueueEntry;
        QueuePopupContent.PlayRequested += async index => await GuardAsync(async () =>
        {
            if (index < 0 || index >= Queue.Count) return;
            _queueIndex = index; _queueDetached = false; await PlayTrackAsync(Queue[index]);
        });
        QueuePopupContent.CreateRequested += async (name, useQueue) => await GuardAsync(async () =>
        {
            var store = RequireLocalPlaylists(); var snapshot = useQueue ? Queue.ToArray() : [];
            var created = await RunPlaylistWriteAsync(() => store.Create(name, snapshot));
            if (store != _localPlaylists || _closing) return;
            RefreshLocalPlaylists(created.Id); Status = $"已保存本地列表 · {created.Name} · {created.Tracks.Count:N0} 首";
        });
        QueuePopupContent.RenameRequested += async (id, name) => await EditLocalPlaylistAsync(store => store.Rename(id, name), id);
        QueuePopupContent.DeleteRequested += async id => await EditLocalPlaylistAsync(store => store.Delete(id));
        QueuePopupContent.UpdateRequested += async id => { var tracks = Queue.ToArray(); await EditLocalPlaylistAsync(store => store.ReplaceTracks(id, tracks), id); };
        QueuePopupContent.LoadRequested += id => LoadLocalPlaylist(id);
        QueuePopup.Opened += (_, _) =>
        {
            QueuePopupContent.Height = Math.Clamp(ActualHeight - 150, 280, 570);
            RefreshLocalPlaylists(); UpdateQueueHeader(); ShowPlayerChrome();
        };
        QueuePopup.Child.PreviewMouseDown += (_, _) => _chromeKeyboardInteraction = false;
        QueuePopup.Child.PreviewKeyDown += (_, e) => { _chromeKeyboardInteraction = true; if (e.Key == Key.Escape) { QueuePopup.IsOpen = false; e.Handled = true; } };
    }

    private void Queue_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_replacingQueue) return;
        _queueRevision++;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                _queueIndex = QueueEditMath.Insert(_queueIndex, _queueDetached, e.NewStartingIndex, e.NewItems!.Count); break;
            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < e.OldItems!.Count; i++) (_queueIndex, _queueDetached) = QueueEditMath.Remove(_queueIndex, _queueDetached, e.OldStartingIndex); break;
            case NotifyCollectionChangedAction.Move:
                _queueIndex = QueueEditMath.Move(_queueIndex, _queueDetached, e.OldStartingIndex, e.NewStartingIndex, Queue.Count); break;
            case NotifyCollectionChangedAction.Reset:
                _queueIndex = -1; _queueDetached = true; break;
        }
        _queueScopeLabel = "自定义队列"; _queueSourceCount = Queue.Count;
        UpdateQueueHeader();
    }
    private void UpdateQueueHeader()
    {
        if (QueuePopupContent is null) return;
        Notify(nameof(QueueScopeLabel)); Notify(nameof(QueueSummaryLabel));
        QueuePopupContent.UpdateSummary(QueueScopeLabel, Current, _playing);
        if (_section == "queue") PageSubtitle = $"{QueueSummaryLabel} · 双击立即播放";
    }
    private void ReplaceQueueWithoutPlayback(IEnumerable<Track> tracks, string label)
    {
        var list = tracks.ToList();
        _queueAlbumId = "";
        _replacingQueue = true;
        try { Replace(Queue, list); }
        finally { _replacingQueue = false; }
        _queueIndex = Current is null ? -1 : list.FindIndex(track => track.Id == Current.Id);
        _queueDetached = _queueIndex < 0;
        _queueSourceCount = Queue.Count; _queueScopeLabel = label; _queueRevision++;
        UpdateQueueHeader();
    }
    private void RemoveQueueAt(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        var track = Queue[index]; Queue.RemoveAt(index);
        Status = "已移出队列 · " + track.Title + (ReferenceEquals(track, Current) ? " · 当前音乐继续播放" : "");
    }
    private void MoveQueueEntry(int from, int to)
    {
        if (from < 0 || from >= Queue.Count || to < 0 || to >= Queue.Count || from == to) return;
        Queue.Move(from, to); Status = "已调整播放顺序。";
    }
    private void QueuePopup_Click(object sender, RoutedEventArgs e)
    {
        OutputPopup.IsOpen = false; QueuePopup.IsOpen = !QueuePopup.IsOpen;
    }
    private void RefreshLocalPlaylists(string? selectedId = null)
    {
        QueuePopupContent.SetPlaylists(_localPlaylists?.GetAll() ?? [], selectedId);
        if (_localPlaylists?.LoadWarning is { } warning) Status = warning;
    }
    private void InitializePlaylistStore()
    {
        var candidate = new LocalPlaylistService(_settings);
        // Reuse account stores across reconnects so an in-flight save cannot publish
        // into an abandoned instance while a newly loaded stale instance overwrites it.
        if (!_playlistStores.TryGetValue(candidate.StoragePath, out _localPlaylists))
            _playlistStores[candidate.StoragePath] = _localPlaylists = candidate;
        RefreshLocalPlaylists();
    }
    private LocalPlaylistService RequireLocalPlaylists()
    {
        if (string.IsNullOrWhiteSpace(_settings.ServerUrl) || string.IsNullOrWhiteSpace(_settings.UserId)) throw new InvalidOperationException("连接 Emby 后即可按账号保存本地播放列表。");
        return _localPlaylists ??= new LocalPlaylistService(_settings);
    }
    private async Task EditLocalPlaylistAsync(Action<LocalPlaylistService> edit, string? selectedId = null) => await GuardAsync(async () =>
    {
        var store = RequireLocalPlaylists(); await RunPlaylistWriteAsync(() => { edit(store); return true; });
        if (_closing || store != _localPlaylists) return;
        RefreshLocalPlaylists(selectedId); Status = "本地播放列表已更新。";
    });
    private void LoadLocalPlaylist(string id)
    {
        var list = _localPlaylists?.GetAll().FirstOrDefault(x => x.Id == id);
        if (list is null) return;
        var tracks = list.Tracks.Select(x => x.ToTrack()).ToArray();
        if (_emby is { } client)
            foreach (var track in tracks) track.ImageUrl = client.GetImageUrl(track.AlbumId.Length > 0 ? track.AlbumId : track.Id);
        ReplaceQueueWithoutPlayback(tracks, "本地列表 · " + list.Name);
        Status = $"已载入 {list.Name} · {tracks.Length:N0} 首" + (Current is not null ? " · 当前音乐继续播放。" : "。");
    }
    private void PopulateLocalPlaylistMenu(MenuItem menu, Track? track)
    {
        menu.Items.Clear(); menu.IsEnabled = track is not null && PlaylistTrack.CanSave(track);
        if (!menu.IsEnabled) return;
        var store = _localPlaylists;
        foreach (var list in store?.GetAll() ?? [])
        {
            var entry = new MenuItem { Header = list.DisplayName };
            entry.Click += async (_, _) => await GuardAsync(async () =>
            {
                if (store != _localPlaylists || store is null) return;
                await RunPlaylistWriteAsync(() => { store.AddTracks(list.Id, [track!]); return true; });
                if (store != _localPlaylists || _closing) return;
                RefreshLocalPlaylists(list.Id); Status = $"已添加到 {list.Name} · {track!.Title}";
            });
            menu.Items.Add(entry);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "新建列表并添加…" };
        create.Click += async (_, _) => await GuardAsync(async () =>
        {
            var activeStore = RequireLocalPlaylists();
            var name = TextPromptWindow.Ask(this, "新建本地播放列表", "列表名称");
            if (name is null || activeStore != _localPlaylists || _closing) return;
            var created = await RunPlaylistWriteAsync(() => activeStore.Create(name, [track!]));
            if (activeStore != _localPlaylists || _closing) return;
            RefreshLocalPlaylists(created.Id); Status = "已添加到本地列表 · " + created.Name;
        });
        menu.Items.Add(create);
    }
    private void TrackMenu_Opened(object sender, RoutedEventArgs e) => PopulateLocalPlaylistMenu(TrackLocalPlaylistMenu, TrackList.SelectedItem as Track);
    private void LibraryTrackMenu_Opened(object sender, RoutedEventArgs e) => PopulateLocalPlaylistMenu(LibraryLocalPlaylistMenu, (LibraryList.SelectedItem as LibraryItem)?.Track);

    private Task<T> RunPlaylistWriteAsync<T>(Func<T> write)
    {
        if (_closing) throw new OperationCanceledException();
        var previous = _playlistWrites;
        async Task<T> Run()
        {
            try { await previous; } catch { /* The originating command already reports a failed save. */ }
            return await Task.Run(write);
        }
        var pending = Run(); _playlistWrites = pending; return pending;
    }

    private async Task OpenAlbumAsync(Album album)
    {
        var origin = CaptureNavigationFrame();
        _albumQueueCts?.Cancel(); _albumQueueCts?.Dispose();
        var cancellation = _albumQueueCts = new CancellationTokenSource();
        var request = ++_albumQueueVersion; var connection = _connectVersion; var revision = _queueRevision;
        var client = _emby;
        _albumId = album.Id; _page = 0; _search = ""; SearchBox.Text = ""; _searchTimer.Stop();
        PageTitle = album.Title; PageSubtitle = album.Artist + (album.Year is { } year ? $" · {year}" : "");
        ShowPageFrom("album", origin);
        if (client is null) { Status = "请先连接 Emby。"; return; }
        var pageTask = LoadTracksAsync();
        var queueTask = AlbumQueueLoader.LoadAsync((start, count, token) => client.GetTracksAsync(start: start, limit: count, albumId: album.Id, cancellationToken: token), cancellation.Token);
        // Entering immersive lyrics or changing the visible page cancels only that
        // page's request. It must not cancel preparation of the selected album queue.
        await Task.WhenAll(ObserveAlbumPageAsync(pageTask), queueTask);
        if (_closing || cancellation.IsCancellationRequested || request != _albumQueueVersion || client != _emby || connection != _connectVersion || revision != _queueRevision || _albumId != album.Id) return;
        ReplaceQueueWithoutPlayback(queueTask.Result, "专辑 · " + album.Title);
        _queueAlbumId = album.Id;
        Status = $"专辑已加入当前队列 · {Queue.Count:N0} 首" + (Current is not null ? " · 当前音乐继续播放" : "");
    }

    private static async Task ObserveAlbumPageAsync(Task page)
    {
        try { await page; }
        catch (OperationCanceledException) { }
    }

    private async Task PlayVisibleTrackAsync(Track track)
    {
        if (_section != "album" || string.IsNullOrEmpty(_albumId) || _emby is not { } client)
        { await StartQueueAsync(Tracks, first: track); return; }
        var albumId = _albumId; var connection = _connectVersion;
        var index = _queueAlbumId == albumId ? Queue.ToList().FindIndex(item => item.Id == track.Id) : -1;
        if (index < 0)
        {
            _queueSelectionCts?.Cancel(); _queueSelectionCts?.Dispose();
            var cancellation = _queueSelectionCts = new CancellationTokenSource();
            var complete = await AlbumQueueLoader.LoadAsync((start, count, token) => client.GetTracksAsync(start: start, limit: count, albumId: albumId, cancellationToken: token), cancellation.Token);
            if (cancellation.IsCancellationRequested || _closing || client != _emby || connection != _connectVersion || albumId != _albumId) return;
            ReplaceQueueWithoutPlayback(complete, "专辑 · " + PageTitle); _queueAlbumId = albumId;
            index = Queue.ToList().FindIndex(item => item.Id == track.Id);
        }
        if (index < 0) { Status = "这首歌曲已不在当前专辑中。"; return; }
        _queueIndex = index; _queueDetached = false;
        await PlayTrackAsync(Queue[index]);
    }
}
