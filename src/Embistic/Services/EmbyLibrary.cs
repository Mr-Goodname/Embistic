using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Embistic.Models;

namespace Embistic.Services;

public sealed partial class EmbyClient
{
    private const string LibraryFields = "Path,ChildCount,RecursiveItemCount,MediaType,Artists,AlbumArtists,Genres,People,Studios,Tags,UserData,ProductionYear,MediaSources,MediaStreams";
    private readonly SemaphoreSlim _libraryCacheGate = new(1, 1);
    private IReadOnlyList<Track>? _libraryTrackCache;
    private DateTimeOffset _libraryCacheTime;

    public async Task<IReadOnlyList<LibraryItem>> GetMusicRootsAsync(CancellationToken cancellationToken = default)
    {
        RequireUser();
        using var document = await GetJsonAsync($"Users/{Uri.EscapeDataString(_settings.UserId)}/Views", cancellationToken);
        return Array(document.RootElement, "Items")
            .Where(item => String(item, "CollectionType").Equals("music", StringComparison.OrdinalIgnoreCase))
            .Select(ParseLibraryItem).ToArray();
    }

    public async Task<PagedResult<LibraryItem>> BrowseFolderAsync(string? parentId, int start = 0, int limit = 200, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parentId))
        {
            var roots = await GetMusicRootsAsync(cancellationToken);
            return new PagedResult<LibraryItem>(roots, roots.Count);
        }
        var query = new Dictionary<string, string?>
        {
            ["ParentId"] = parentId, ["Recursive"] = "false", ["SortBy"] = "IsFolder,SortName", ["SortOrder"] = "Ascending",
            ["StartIndex"] = Math.Max(0, start).ToString(CultureInfo.InvariantCulture), ["Limit"] = Math.Clamp(limit, 1, 1000).ToString(CultureInfo.InvariantCulture),
            ["IncludeItemTypes"] = "Folder,CollectionFolder,MusicAlbum,Audio", ["Fields"] = LibraryFields
        };
        using var document = await GetJsonAsync(ItemsPath + Query(query), cancellationToken);
        var items = Array(document.RootElement, "Items").Select(ParseLibraryItem).ToArray();
        return new(items, Integer(document.RootElement, "TotalRecordCount") ?? items.Length);
    }

    public Task<IReadOnlyList<LibraryItem>> GetArtistsAsync(bool albumArtists = false, CancellationToken cancellationToken = default) =>
        ReadLibraryCollectionAsync(albumArtists ? "Artists/AlbumArtists" : "Artists", albumArtists ? "albumartists" : "artists", cancellationToken);

    public Task<IReadOnlyList<LibraryItem>> GetGenresAsync(CancellationToken cancellationToken = default) =>
        ReadLibraryCollectionAsync("MusicGenres", "genres", cancellationToken);

    public async Task<IReadOnlyList<LibraryItem>> GetPlaylistsAsync(CancellationToken cancellationToken = default)
    {
        var playlists = new List<LibraryItem>();
        var offset = 0;
        var total = int.MaxValue;
        while (offset < total && offset < 10000)
        {
            var query = new Dictionary<string, string?>
            {
                ["IncludeItemTypes"] = "Playlist", ["Recursive"] = "true", ["SortBy"] = "SortName", ["SortOrder"] = "Ascending",
                ["Fields"] = LibraryFields, ["Limit"] = "500", ["StartIndex"] = offset.ToString(CultureInfo.InvariantCulture)
            };
            using var document = await GetJsonAsync(ItemsPath + Query(query), cancellationToken);
            var page = Array(document.RootElement, "Items").ToArray();
            playlists.AddRange(page.Where(item => String(item, "MediaType") is "" or "Audio").Select(ParseLibraryItem));
            offset += page.Length;
            total = Integer(document.RootElement, "TotalRecordCount") ?? offset;
            if (page.Length == 0) break;
        }
        return playlists;
    }

    public async Task<PagedResult<Track>> GetPlaylistTracksAsync(string playlistId, int start = 0, int limit = 200, CancellationToken cancellationToken = default)
    {
        ValidateItemId(playlistId);
        var query = new Dictionary<string, string?>
        {
            ["UserId"] = _settings.UserId, ["StartIndex"] = Math.Max(0, start).ToString(CultureInfo.InvariantCulture),
            ["Limit"] = Math.Clamp(limit, 1, 1000).ToString(CultureInfo.InvariantCulture), ["Fields"] = LibraryFields
        };
        using var document = await GetJsonAsync($"Playlists/{Uri.EscapeDataString(playlistId)}/Items" + Query(query), cancellationToken);
        var tracks = Array(document.RootElement, "Items").Where(item => String(item, "Type") is "" or "Audio").Select(ParseLibraryTrack).ToArray();
        return new(tracks, Integer(document.RootElement, "TotalRecordCount") ?? tracks.Length);
    }

    public async Task<LibraryItem> CreatePlaylistAsync(string name, IReadOnlyList<string> trackIds, CancellationToken cancellationToken = default)
    {
        RequireUser();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("请输入播放列表名称。", nameof(name));
        name = name.Trim();
        if (name.Length > 200) throw new ArgumentException("播放列表名称最多 200 个字符。", nameof(name));
        var ids = CleanTrackIds(trackIds);
        var initial = ids.Take(200).ToArray();
        var query = new Dictionary<string, string?> { ["Name"] = name, ["Ids"] = string.Join(',', initial), ["UserId"] = _settings.UserId, ["MediaType"] = "Audio" };
        using var response = await _http.PostAsync("Playlists" + Query(query), null, cancellationToken);
        EnsureSuccess(response);
        using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var id = String(document.RootElement, "Id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Emby 未返回新播放列表，请刷新播放列表页面。");
        var result = new LibraryItem { Id = id, Name = name, Type = "Playlist", IsFolder = true, Count = ids.Length };
        if (ids.Length > initial.Length)
        {
            try { await AddPlaylistTracksAsync(id, ids.Skip(initial.Length).ToArray(), cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            { throw new InvalidOperationException("播放列表已创建，但后续歌曲未全部加入，请在播放列表中重试。"); }
        }
        return result;
    }

    public async Task AddPlaylistTracksAsync(string playlistId, IReadOnlyList<string> trackIds, CancellationToken cancellationToken = default)
    {
        RequireUser();
        ValidateItemId(playlistId);
        var ids = CleanTrackIds(trackIds);
        for (var start = 0; start < ids.Length; start += 200)
        {
            var query = new Dictionary<string, string?>
            {
                ["Ids"] = string.Join(',', ids.Skip(start).Take(200)), ["UserId"] = _settings.UserId
            };
            using var response = await _http.PostAsync($"Playlists/{Uri.EscapeDataString(playlistId)}/Items" + Query(query), null, cancellationToken);
            EnsureSuccess(response);
        }
    }

    public async Task<IReadOnlyList<LibraryItem>> GetLibraryGroupsAsync(string kind, CancellationToken cancellationToken = default)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();
        if (kind is "artists" or "artist") return await GetArtistsAsync(cancellationToken: cancellationToken);
        if (kind is "albumartists" or "albumartist") return await GetArtistsAsync(true, cancellationToken);
        if (kind is "genres" or "genre") return await GetGenresAsync(cancellationToken);
        var tracks = await ReadLibraryTracksAsync(cancellationToken);
        IEnumerable<(string Key, Track Track)> pairs = kind switch
        {
            "years" or "year" => tracks.Where(track => track.Year is > 0).Select(track => (track.Year!.Value.ToString(CultureInfo.InvariantCulture), track)),
            "composers" or "composer" => tracks.SelectMany(track => track.Composers.Select(name => (name, track))),
            "labels" or "label" => tracks.SelectMany(track => track.Labels.Select(name => (name, track))),
            "compilations" or "compilation" => tracks.Where(track => track.IsCompilation).Select(track => (string.IsNullOrWhiteSpace(track.Album) ? "未命名合辑" : track.Album, track)),
            _ => throw new ArgumentException("不支持的音乐分类。", nameof(kind))
        };
        var groups = pairs.Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .GroupBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new LibraryItem
            {
                Id = "group:" + kind + ":" + group.Key, Name = group.Key, Type = "Group", GroupKind = kind, GroupKey = group.Key,
                IsFolder = true, Count = group.Select(pair => pair.Track.Id).Distinct().Count(),
                ImageUrl = group.Select(pair => pair.Track.ImageUrl).FirstOrDefault(url => !string.IsNullOrEmpty(url)) ?? "",
                TrackIds = group.Select(pair => pair.Track.Id).Distinct().ToArray()
            });
        return kind is "years" or "year" ? groups.OrderByDescending(item => item.Name, StringComparer.Ordinal).ToArray()
            : groups.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<Track>> GetLibraryItemTracksAsync(LibraryItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Track is { } track) return [track];
        if (item.Type == "Group")
        {
            var ids = item.TrackIds.ToHashSet(StringComparer.Ordinal);
            return (await ReadLibraryTracksAsync(cancellationToken)).Where(track => ids.Contains(track.Id)).ToArray();
        }
        var results = new List<Track>();
        var total = int.MaxValue;
        while (results.Count < total && results.Count < 100000)
        {
            if (item.Type == "Playlist")
            {
                var page = await GetPlaylistTracksAsync(item.Id, results.Count, 1000, cancellationToken);
                total = page.TotalCount;
                results.AddRange(page.Items);
                if (page.Items.Count == 0) break;
                continue;
            }
            var query = new Dictionary<string, string?>
            {
                ["IncludeItemTypes"] = "Audio", ["Recursive"] = "true", ["StartIndex"] = results.Count.ToString(CultureInfo.InvariantCulture),
                ["Limit"] = "1000", ["Fields"] = LibraryFields, ["SortBy"] = "ParentIndexNumber,IndexNumber,SortName", ["SortOrder"] = "Ascending"
            };
            if (item.Type == "MusicArtist") query[item.GroupKind == "albumartists" ? "AlbumArtistIds" : "ArtistIds"] = item.Id;
            else if (item.Type == "MusicGenre") query["GenreIds"] = item.Id;
            else query["ParentId"] = item.Id;
            using var document = await GetJsonAsync(ItemsPath + Query(query), cancellationToken);
            var pageItems = Array(document.RootElement, "Items").Select(ParseLibraryTrack).ToArray();
            total = Integer(document.RootElement, "TotalRecordCount") ?? pageItems.Length;
            results.AddRange(pageItems);
            if (pageItems.Length == 0) break;
        }
        return results;
    }

    public async Task<ListeningStats> GetListeningStatsAsync(CancellationToken cancellationToken = default)
    {
        var serverTracks = await ReadLibraryTracksAsync(cancellationToken);
        var history = new ListeningHistoryService(_settings).GetEntries().ToDictionary(entry => entry.TrackId, StringComparer.Ordinal);
        var tracks = serverTracks.Select(CloneTrack).ToList();
        foreach (var track in tracks)
        {
            if (!history.Remove(track.Id, out var local)) continue;
            track.PlayCount = Math.Max(track.PlayCount, local.PlayCount);
            if (track.LastPlayedDate is null || local.LastPlayedDate > track.LastPlayedDate) track.LastPlayedDate = local.LastPlayedDate;
        }
        tracks.AddRange(history.Values.Select(entry => new Track
        {
            Id = entry.TrackId, Title = entry.Title, Artist = entry.Artist, Album = entry.Album, AlbumId = entry.AlbumId,
            Container = entry.Container, DurationMs = entry.DurationMs, PlayCount = entry.PlayCount, LastPlayedDate = entry.LastPlayedDate
        }));
        var recent = tracks.Where(track => track.LastPlayedDate.HasValue).OrderByDescending(track => track.LastPlayedDate).Take(24).ToArray();
        var most = tracks.Where(track => track.PlayCount > 0).OrderByDescending(track => track.PlayCount).ThenByDescending(track => track.LastPlayedDate).Take(24).ToArray();
        var albums = tracks.Where(track => !string.IsNullOrWhiteSpace(track.Album))
            .GroupBy(track => !string.IsNullOrWhiteSpace(track.AlbumId) ? track.AlbumId : track.Artist + "|" + track.Album, StringComparer.Ordinal)
            .Select(group => new
            {
                Album = new Album
                {
                    Id = group.First().AlbumId, Title = group.First().Album, Artist = group.First().Artist, Year = group.Select(track => track.Year).FirstOrDefault(year => year.HasValue),
                    ImageUrl = group.Select(track => track.ImageUrl).FirstOrDefault(image => !string.IsNullOrEmpty(image)) ?? ""
                },
                Count = group.Sum(track => (long)track.PlayCount),
                Last = group.Max(track => track.LastPlayedDate)
            }).ToArray();
        return new ListeningStats(recent, most,
            albums.Where(album => album.Last.HasValue).OrderByDescending(album => album.Last).Take(12).Select(album => album.Album).ToArray(),
            albums.Where(album => album.Count > 0).OrderByDescending(album => album.Count).ThenByDescending(album => album.Last).Take(12).Select(album => album.Album).ToArray());
    }

    public void InvalidateLibraryCache() => _libraryCacheTime = DateTimeOffset.MinValue;

    private async Task<IReadOnlyList<LibraryItem>> ReadLibraryCollectionAsync(string route, string groupKind, CancellationToken cancellationToken)
    {
        var items = new List<LibraryItem>();
        var total = int.MaxValue;
        while (items.Count < total && items.Count < 10000)
        {
            var query = new Dictionary<string, string?>
            {
                ["UserId"] = _settings.UserId, ["Recursive"] = "true", ["SortBy"] = "SortName", ["SortOrder"] = "Ascending", ["Fields"] = LibraryFields,
                ["StartIndex"] = items.Count.ToString(CultureInfo.InvariantCulture), ["Limit"] = "500"
            };
            using var document = await GetJsonAsync(route + Query(query), cancellationToken);
            var page = Array(document.RootElement, "Items").Select(ParseLibraryItem).ToArray();
            foreach (var item in page) { item.GroupKind = groupKind; item.GroupKey = item.Name; }
            total = Integer(document.RootElement, "TotalRecordCount") ?? page.Length;
            items.AddRange(page);
            if (page.Length == 0) break;
        }
        return items;
    }

    private async Task<IReadOnlyList<Track>> ReadLibraryTracksAsync(CancellationToken cancellationToken)
    {
        if (_libraryTrackCache is not null && DateTimeOffset.UtcNow - _libraryCacheTime < TimeSpan.FromMinutes(10)) return _libraryTrackCache;
        await _libraryCacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_libraryTrackCache is not null && DateTimeOffset.UtcNow - _libraryCacheTime < TimeSpan.FromMinutes(10)) return _libraryTrackCache;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var tracks = new List<Track>();
            var total = int.MaxValue;
            while (tracks.Count < total && tracks.Count < 100000)
            {
                var query = new Dictionary<string, string?>
                {
                    ["IncludeItemTypes"] = "Audio", ["Recursive"] = "true", ["SortBy"] = "SortName", ["SortOrder"] = "Ascending",
                    ["StartIndex"] = tracks.Count.ToString(CultureInfo.InvariantCulture), ["Limit"] = "1000", ["Fields"] = LibraryFields
                };
                using var document = await GetJsonAsync(ItemsPath + Query(query), deadline.Token);
                var page = Array(document.RootElement, "Items").Select(ParseLibraryTrack).ToArray();
                total = Integer(document.RootElement, "TotalRecordCount") ?? page.Length;
                tracks.AddRange(page);
                if (page.Length == 0) break;
            }
            _libraryTrackCache = tracks;
            _libraryCacheTime = DateTimeOffset.UtcNow;
            return tracks;
        }
        finally { _libraryCacheGate.Release(); }
    }

    private LibraryItem ParseLibraryItem(JsonElement item)
    {
        var type = String(item, "Type");
        return new LibraryItem
        {
            Id = String(item, "Id"), Name = String(item, "Name"), Type = type, Artist = Artist(item) is "未知艺术家" ? "" : Artist(item),
            ImageUrl = ImageFor(item), Path = String(item, "Path"), IsFolder = type != "Audio",
            Count = Integer(item, "ChildCount") ?? Integer(item, "RecursiveItemCount") ?? Integer(item, "SongCount") ?? 0,
            Track = type == "Audio" ? ParseLibraryTrack(item) : null
        };
    }

    private Track ParseLibraryTrack(JsonElement item)
    {
        var track = ParseTrack(item);
        var data = Property(item, "UserData");
        track.PlayCount = Math.Max(0, Integer(data, "PlayCount") ?? 0);
        track.LastPlayedDate = DateTimeOffset.TryParse(String(data, "LastPlayedDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var last) ? last : null;
        track.Year = Integer(item, "ProductionYear");
        track.Path = String(item, "Path");
        track.Genres = Array(item, "Genres").Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        track.Composers = Array(item, "People").Where(person => String(person, "Type").Equals("Composer", StringComparison.OrdinalIgnoreCase))
            .Select(person => String(person, "Name"))
            .Concat(Array(item, "Composers").Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        track.Labels = Array(item, "Studios").Select(studio => studio.ValueKind == JsonValueKind.String ? studio.GetString()! : String(studio, "Name"))
            .Concat(Array(item, "Labels").Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var albumArtists = Array(item, "AlbumArtists").Select(artist => artist.ValueKind == JsonValueKind.String ? artist.GetString()! : String(artist, "Name")).Append(String(item, "AlbumArtist"));
        track.IsCompilation = Boolean(item, "IsCompilation") || albumArtists.Any(name => name.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Various", StringComparison.OrdinalIgnoreCase) || name.Equals("V.A.", StringComparison.OrdinalIgnoreCase))
            || Array(item, "Tags").Any(tag => tag.ValueKind == JsonValueKind.String && (tag.GetString()?.Equals("Compilation", StringComparison.OrdinalIgnoreCase) == true));
        return track;
    }

    private static Track CloneTrack(Track track) => new()
    {
        Id = track.Id, Title = track.Title, Artist = track.Artist, Album = track.Album, AlbumId = track.AlbumId, DurationMs = track.DurationMs,
        Container = track.Container, BitRate = track.BitRate, SampleRate = track.SampleRate, BitDepth = track.BitDepth, Index = track.Index,
        IsFavorite = track.IsFavorite, ImageUrl = track.ImageUrl, PlayCount = track.PlayCount, LastPlayedDate = track.LastPlayedDate, Year = track.Year,
        Genres = track.Genres.ToList(), Composers = track.Composers.ToList(), Labels = track.Labels.ToList(), IsCompilation = track.IsCompilation, Path = track.Path
    };

    private static void ValidateItemId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) throw new ArgumentException("请选择有效的 Emby 条目。");
    }

    private static string[] CleanTrackIds(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return ids.Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 200).Distinct(StringComparer.Ordinal).Take(100000).ToArray();
    }
}
