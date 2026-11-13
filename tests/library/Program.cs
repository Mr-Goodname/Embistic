using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Embistic.Models;
using Embistic.Services;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

if (args.Contains("--live"))
{
    using var live = new EmbyClient(SettingsService.Load());
    var liveRoots = await live.GetMusicRootsAsync();
    Check(liveRoots.Count > 0, "Actual user music library views");
    var folders = await live.BrowseFolderAsync(liveRoots[0].Id, limit: 12);
    Check(folders.TotalCount > 0 && folders.Items.All(item => item.IsFolder || item.Track is not null), "Actual server folder hierarchy");
    Console.WriteLine($"Music roots: {liveRoots.Count}; first-level items: {folders.TotalCount}");
    var artists = await live.GetArtistsAsync();
    var albumArtists = await live.GetArtistsAsync(true);
    var genres = await live.GetGenresAsync();
    Check(artists.Count > 0 && albumArtists.Count > 0 && genres.Count > 0, "Actual artist, album-artist and genre indexes");
    Console.WriteLine($"Artists: {artists.Count}; album artists: {albumArtists.Count}; genres: {genres.Count}; playlists: {(await live.GetPlaylistsAsync()).Count}");
    var liveYears = await live.GetLibraryGroupsAsync("years");
    var liveStats = await live.GetListeningStatsAsync();
    Check(liveYears.All(item => item.TrackIds.Count > 0), "Year grouping retains playable track IDs");
    Console.WriteLine($"Year groups: {liveYears.Count}; recent tracks: {liveStats.RecentTracks.Count}; most-played tracks: {liveStats.MostPlayedTracks.Count}");
    if (artists.FirstOrDefault() is { } artist)
        Check((await live.GetLibraryItemTracksAsync(artist)).Count > 0, "Actual artist expands to music tracks");
    return;
}

var clock = new ManualClock();
var dataDirectory = Path.Combine(Path.GetTempPath(), "Embistic-library-checks-" + Guid.NewGuid().ToString("N"));
var historySettings = new AppSettings { ServerUrl = "http://fixture.invalid", UserId = "history-user" };
var history = new ListeningHistoryService(historySettings, dataDirectory, clock);
var first = new Track { Id = "first", Title = "First", Artist = "Singer", Album = "Alpha", AlbumId = "alpha", DurationMs = 120000, ImageUrl = "http://private.invalid/?api_key=private-token" };
var changed = 0;
history.Changed += () => changed++;
history.BeginTrack(first);
history.UpdatePlayback(first, 0, true);
clock.Advance(100);
history.UpdatePlayback(first, 90000, true);
history.EndTrack(first, 120000);
Check(history.GetEntries().Count == 0, "Seeking to the end cannot count a preview as a listen");
history.BeginTrack(first);
history.UpdatePlayback(first, 0, true);
for (var second = 1; second <= 29; second++) { clock.Advance(1000); history.UpdatePlayback(first, second * 1000, true); }
Check(history.GetEntries().Count == 0, "Long track needs thirty seconds of actual listening");
clock.Advance(1000); history.UpdatePlayback(first, 30000, true);
Check(history.GetEntries().Single().PlayCount == 1 && changed == 1, "Qualified listen counted exactly once");
for (var second = 31; second <= 45; second++) { clock.Advance(1000); history.UpdatePlayback(first, second * 1000, true); }
Check(history.GetEntries().Single().PlayCount == 1, "Further progress does not duplicate the current session");
history.EndTrack(first, 45000);
Check(!File.ReadAllText(history.StoragePath).Contains("private-token"), "History contains no authenticated artwork or stream URLs");
var restarted = new ListeningHistoryService(historySettings, dataDirectory, clock);
Check(restarted.GetEntries().Single().Title == "First", "Qualified history survives process restart");
var otherUser = new ListeningHistoryService(new AppSettings { ServerUrl = historySettings.ServerUrl, UserId = "different-user" }, dataDirectory, clock);
Check(otherUser.GetEntries().Count == 0, "Listening history is isolated by server and user");

var shortTrack = new Track { Id = "short", Title = "Short", DurationMs = 10000 };
history.BeginTrack(shortTrack); history.UpdatePlayback(shortTrack, 0, true);
for (var second = 1; second <= 5; second++) { clock.Advance(1000); history.UpdatePlayback(shortTrack, second * 1000, true); }
Check(history.GetEntries().Any(entry => entry.TrackId == "short"), "Half of a short track qualifies");
var paused = new Track { Id = "paused", DurationMs = 120000 };
history.BeginTrack(paused); history.UpdatePlayback(paused, 0, false);
for (var second = 1; second <= 60; second++) { clock.Advance(1000); history.UpdatePlayback(paused, 30000, false); }
history.EndTrack(paused, 30000);
Check(history.GetEntries().All(entry => entry.TrackId != "paused"), "Paused reading time cannot count as listening");
var intactHistory = File.ReadAllText(history.StoragePath);
File.WriteAllText(history.StoragePath, "[null]");
Check(new ListeningHistoryService(historySettings, dataDirectory, clock).GetEntries().Count == 0, "Malformed null history entries are ignored");
File.WriteAllText(history.StoragePath, intactHistory);

var tcp = new TcpListener(IPAddress.Loopback, 0);
tcp.Start(); var port = ((IPEndPoint)tcp.LocalEndpoint).Port; tcp.Stop();
var settings = new AppSettings { ServerUrl = $"http://127.0.0.1:{port}", ApiKey = "fixture-token", UserId = "library-fixture-" + Guid.NewGuid().ToString("N") };
using var listener = new HttpListener();
listener.Prefixes.Add(settings.ServerUrl + "/"); listener.Start();
var observedCreate = false;
var addBatches = new List<int>();
const string tracksJson = """
{"TotalRecordCount":4,"Items":[
 {"Id":"1","Type":"Audio","Name":"First","Artists":["Singer A"],"Album":"Alpha","AlbumId":"10","ProductionYear":2020,"RunTimeTicks":1200000000,"Genres":["Anime"],"People":[{"Name":"Composer A","Type":"Composer"}],"Studios":[{"Name":"Label A"}],"Path":"/music/a/first.flac","UserData":{"PlayCount":2,"LastPlayedDate":"2024-01-01T00:00:00Z"}},
 {"Id":"2","Type":"Audio","Name":"Second","Artists":["Singer A"],"Album":"Alpha","AlbumId":"10","ProductionYear":2020,"RunTimeTicks":1200000000,"Genres":["Anime"],"UserData":{"PlayCount":7,"LastPlayedDate":"2025-01-01T00:00:00Z"}},
 {"Id":"3","Type":"Audio","Name":"Third","Artists":["Singer B"],"Album":"Beta","AlbumId":"20","AlbumArtist":"Various Artists","ProductionYear":2021,"RunTimeTicks":1200000000,"UserData":{"PlayCount":1,"LastPlayedDate":"2026-01-01T00:00:00Z"}},
 {"Id":"4","Type":"Audio","Name":"Fourth","Artists":["Singer B"],"Album":"Beta","AlbumId":"20","ProductionYear":2019,"RunTimeTicks":1200000000,"UserData":{"PlayCount":0}}
]}
""";
using var serverStop = new CancellationTokenSource();
var server = Task.Run(async () =>
{
    while (!serverStop.IsCancellationRequested)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync().WaitAsync(serverStop.Token); }
        catch (OperationCanceledException) { return; }
        catch (HttpListenerException) when (serverStop.IsCancellationRequested) { return; }
        var request = context.Request;
        Check(request.Headers["X-Emby-Token"] == "fixture-token", "Library requests retain connection authentication");
        var path = request.Url!.AbsolutePath;
        string response;
        if (path.EndsWith("/Views"))
            response = """{"Items":[{"Id":"root","Name":"Music","Type":"CollectionFolder","CollectionType":"music"},{"Id":"video","Name":"Video","CollectionType":"movies"}]}""";
        else if (request.HttpMethod == "POST" && path == "/Playlists")
        {
            observedCreate = request.QueryString["Name"] == "雨 & 花" && request.QueryString["Ids"]?.Split(',').Length == 200 && request.QueryString["MediaType"] == "Audio";
            response = """{"Id":"playlist-new"}""";
        }
        else if (request.HttpMethod == "POST" && path == "/Playlists/playlist-new/Items")
        {
            addBatches.Add(request.QueryString["Ids"]!.Split(',').Length);
            response = "{}";
        }
        else if (path == "/Playlists/playlist/Items")
            response = """{"TotalRecordCount":2,"Items":[{"Id":"2","Type":"Audio","Name":"Second"},{"Id":"1","Type":"Audio","Name":"First"}]}""";
        else if (path == "/Artists" || path == "/Artists/AlbumArtists")
            response = """{"TotalRecordCount":1,"Items":[{"Id":"artist","Name":"Singer A","Type":"MusicArtist","ChildCount":2}]}""";
        else if (path == "/MusicGenres")
            response = """{"TotalRecordCount":1,"Items":[{"Id":"genre","Name":"Anime","Type":"MusicGenre"}]}""";
        else if (request.QueryString["IncludeItemTypes"] == "Playlist")
            response = """{"Items":[{"Id":"playlist","Name":"Favorite Mix","Type":"Playlist","MediaType":"Audio"},{"Id":"video-playlist","Name":"Video","Type":"Playlist","MediaType":"Video"}]}""";
        else if (request.QueryString["ParentId"] == "root" && request.QueryString["Recursive"] == "false")
        {
            Check(request.QueryString["StartIndex"] == "2" && request.QueryString["Limit"] == "2", "Folder pagination preserves requested offset");
            response = """{"TotalRecordCount":4,"Items":[{"Id":"folder","Name":"Album folder","Type":"Folder","Path":"/music/album","ChildCount":2},{"Id":"leaf","Name":"Track","Type":"Audio","Path":"/music/leaf.flac"}]}""";
        }
        else response = tracksJson;
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(response));
        context.Response.Close();
    }
});

using var client = new EmbyClient(settings);
var roots = await client.GetMusicRootsAsync();
Check(roots.Count == 1 && roots[0].Id == "root", "Only user music views become roots");
var folder = await client.BrowseFolderAsync("root", 2, 2);
Check(folder.TotalCount == 4 && folder.Items[0].Path == "/music/album" && !folder.Items[1].IsFolder && folder.Items[1].Track is not null, "Actual folder paths and audio leaves are retained");
Check((await client.GetArtistsAsync()).Single().GroupKind == "artists", "Native artist index");
Check((await client.GetArtistsAsync(true)).Single().GroupKind == "albumartists", "Native album-artist index");
Check((await client.GetGenresAsync()).Single().Name == "Anime", "Native genre index");
Check((await client.GetPlaylistsAsync()).Count == 1, "Non-audio playlists excluded");
Check((await client.GetPlaylistTracksAsync("playlist")).Items.Select(track => track.Id).SequenceEqual(["2", "1"]), "Playlist order preserved");
var created = await client.CreatePlaylistAsync("雨 & 花", Enumerable.Range(1, 450).Select(number => number.ToString()).ToArray());
Check(observedCreate && created.Id == "playlist-new" && addBatches.SequenceEqual([200, 50]), "Playlist creation encodes Unicode and batches long selections");
var years = await client.GetLibraryGroupsAsync("years");
Check(years.Select(item => item.Name).SequenceEqual(["2021", "2020", "2019"]), "Year categories sort newest first");
Check((await client.GetLibraryGroupsAsync("composers")).Single().Name == "Composer A", "Composer metadata groups");
Check((await client.GetLibraryGroupsAsync("labels")).Single().Name == "Label A", "Record-label metadata groups");
Check((await client.GetLibraryGroupsAsync("compilations")).Single().TrackIds.SequenceEqual(["3"]), "Various Artists metadata identifies compilations");
Check((await client.GetLibraryItemTracksAsync(years.First(item => item.Name == "2020"))).Count == 2, "Local categories expand to their exact tracks");
var stats = await client.GetListeningStatsAsync();
Check(stats.RecentTracks[0].Id == "3" && stats.MostPlayedTracks[0].Id == "2", "Server recent date and play count determine song ordering");
Check(stats.RecentAlbums[0].Id == "20" && stats.MostPlayedAlbums[0].Id == "10", "Album history groups by album ID");

var mergedHistory = new ListeningHistoryService(settings, clock: clock);
var ownedHistoryFile = !File.Exists(mergedHistory.StoragePath);
var fourth = new Track { Id = "4", Title = "Fourth", Album = "Beta", AlbumId = "20", Artist = "Singer B", DurationMs = 120000 };
mergedHistory.BeginTrack(fourth); mergedHistory.UpdatePlayback(fourth, 0, true);
for (var second = 1; second <= 30; second++) { clock.Advance(1000); mergedHistory.UpdatePlayback(fourth, second * 1000, true); }
stats = await client.GetListeningStatsAsync();
Check(stats.RecentTracks[0].Id == "4" && stats.MostPlayedTracks.Any(track => track.Id == "4"), "Qualified local plays fill missing server history");
if (ownedHistoryFile) File.Delete(mergedHistory.StoragePath);
serverStop.Cancel(); listener.Stop(); await server;
Console.WriteLine($"Completed: {checks} library and history checks passed.");

sealed class ManualClock : TimeProvider
{
    private long _milliseconds;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => _milliseconds;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(_milliseconds);
    public void Advance(long milliseconds) => _milliseconds += milliseconds;
}
