using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Embistic.Models;
using Embistic.Services;

var checks = 0;
void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException("FAIL: " + description); checks++; Console.WriteLine("PASS: " + description); }
async Task Fails(Func<Task> action, string description) { try { await action(); } catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException) { Check(true, description); return; } throw new InvalidOperationException("FAIL: " + description); }
async Task StorageFails(Func<Task> action, string description) { try { await action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { Check(true, description); return; } throw new InvalidOperationException("FAIL: " + description); }
var temp = Path.Combine(Path.GetTempPath(), "Embistic-network-cache-" + Guid.NewGuid().ToString("N"));
var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
var address = $"http://127.0.0.1:{port}";
var track = new Track { Id = "../../fixture", Title = "雨 & 花", Artist = "歌手 A", Album = "Album", DurationMs = 180000, Container = "flac" };
var bytes = Enumerable.Range(0, 65536).Select(index => (byte)(index % 251)).ToArray();
using var http = new HttpListener(); http.Prefixes.Add(address + "/"); http.Start();
using var stop = new CancellationTokenSource();
var textRequests = 0;
var server = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        HttpListenerContext context;
        try { context = await http.GetContextAsync().WaitAsync(stop.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException) { break; }
        var path = context.Request.Url!.AbsolutePath;
        byte[] payload;
        context.Response.ContentType = "application/json";
        switch (path)
        {
            case "/text": textRequests++; payload = Encoding.UTF8.GetBytes("[by:fixture]\n[00:01.000]网络歌词\n[00:02.000]第二句"); context.Response.ContentType = "text/plain"; break;
            case "/json": payload = Encoding.UTF8.GetBytes("{\"data\":{\"lyric\":\"[00:01.000]JSON 歌词\"}}"); break;
            case "/upper-json": payload = Encoding.UTF8.GetBytes("{\"error\":\"fixture\"}"); context.Response.ContentType = "Application/JSON"; break;
            case "/search": payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { data = new[] { new { id = "wrong", name = "别的歌", artist = track.Artist, duration = 180000 }, new { id = "version", name = track.Title, artist = track.Artist, duration = 240000 }, new { id = "correct", name = track.Title, artist = track.Artist, duration = 180050 } } })); break;
            case "/lyric/correct": payload = Encoding.UTF8.GetBytes("[00:01.000]匹配的版本"); context.Response.ContentType = "text/plain"; break;
            case "/audio": payload = bytes; context.Response.ContentType = "audio/flac"; break;
            case "/empty": payload = Array.Empty<byte>(); context.Response.ContentType = "audio/flac"; break;
            case "/oversized": payload = new byte[2_000_001]; break;
            default: context.Response.StatusCode = 503; payload = Encoding.UTF8.GetBytes("{\"error\":\"fixture\"}"); break;
        }
        context.Response.ContentLength64 = payload.Length;
        try { await context.Response.OutputStream.WriteAsync(payload, stop.Token); } catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException) { }
        context.Response.Close();
    }
});
try
{
    var url = NetworkLyricsProvider.BuildUrl(address + "/text?title={title}&artist={artist}&duration={duration}&id={id}", track, "a/b");
    Check(url.Contains("%26") && url.Contains("a%2Fb") && url.Contains("duration=180"), "Template parameters are encoded individually and duration uses seconds");
    try { NetworkLyricsProvider.BuildUrl("file:///C:/private", track); throw new InvalidOperationException(); } catch (ArgumentException) { Check(true, "Only HTTP lyric interfaces are accepted"); }
    try { NetworkLyricsProvider.BuildUrl(address + "/{unknown}", track); throw new InvalidOperationException(); } catch (ArgumentException) { Check(true, "Unknown template placeholders are rejected"); }
    using (var data = JsonDocument.Parse("{\"items\":[{\"value\":\"ok\"}]}"))
    { Check(NetworkLyricsProvider.ReadPath(data.RootElement, "$.items[0].value").GetString() == "ok", "Nested JSON and indexed fields are supported"); Check(NetworkLyricsProvider.ReadPath(data.RootElement, "items[-1]").ValueKind == JsonValueKind.Undefined, "Invalid response fields return no value"); }
    var direct = new NetworkLyricsSource { Name = "Text fixture", UrlTemplate = address + "/text" };
    Check((await NetworkLyricsProvider.FetchAsync(direct, track)).Lines.Count == 2, "Text LRC with metadata is parsed as lyrics");
    var json = new NetworkLyricsSource { Name = "JSON fixture", UrlTemplate = address + "/json", LyricsPath = "data.lyric" };
    Check((await NetworkLyricsProvider.FetchAsync(json, track)).Lines.Single().Text == "JSON 歌词", "Configured JSON lyric field is parsed");
    var search = new NetworkLyricsSource { Name = "Search fixture", SearchUrlTemplate = address + "/search?title={title}", SearchResultsPath = "data", UrlTemplate = address + "/lyric/{id}", DurationPath = "duration", DurationIsMilliseconds = true };
    Check((await NetworkLyricsProvider.FetchAsync(search, track)).Lines.Single().Text == "匹配的版本", "Search matches title, artist and duration before retrieving ID");
    Check(!(await NetworkLyricsProvider.FetchAsync(new NetworkLyricsSource { UrlTemplate = address + "/oversized" }, track)).IsSynced, "Oversized lyric responses are bounded");
    var lyrics = new LyricsService(Path.Combine(temp, "lyrics"));
    lyrics.ConfigureSources(new[] { new NetworkLyricsSource { UrlTemplate = address + "/failure" }, direct });
    Check((await lyrics.GetLyricsAsync(track)).Source == direct.Name, "Failed custom source falls back to next configured source");
    var before = textRequests; await lyrics.GetLyricsAsync(track); Check(textRequests == before, "Successful custom lyrics are reused from cache");
    lyrics.ConfigureSources(new[] { json });
    Check((await lyrics.GetLyricsAsync(track)).Source == json.Name, "Changing source configuration invalidates previous network cache");
    Check((await lyrics.GetLyricsAsync(track, "[00:01]服务器歌词")).Source == "Emby", "Emby synchronized lyrics remain first choice");
    await lyrics.ImportLyricsAsync(track, "[00:01]手动校正");
    Check((await lyrics.GetLyricsAsync(track, "[00:01]服务器歌词")).Lines.Single().Text == "手动校正", "Manual imports retain priority over all network sources");
    var settings = new AppSettings { ServerUrl = address, UserId = "user-one", DownloadDirectory = Path.Combine(temp, "custom") };
    using var downloads = new DownloadService(settings);
    Check(downloads.DirectoryPath.StartsWith(settings.DownloadDirectory + Path.DirectorySeparatorChar), "Configured custom download root is used");
    var file = await downloads.DownloadAsync(track, address + "/audio");
    Check(File.ReadAllBytes(file).SequenceEqual(bytes), "Complete source audio is cached without alteration");
    Check(downloads.GetTracks().Single().Title == track.Title && downloads.IsDownloaded(track), "Completed downloads are indexed for offline browsing");
    Check(!File.ReadAllText(Path.Combine(downloads.DirectoryPath, "index.json")).Contains(address), "Download index contains no authenticated server URL");
    Check(Path.GetDirectoryName(file) == downloads.DirectoryPath, "Track identifiers cannot traverse outside the cache");
    Check(await downloads.DownloadAsync(track, address + "/failure") == file, "Existing download is reused without a request");
    using (var other = new DownloadService(new AppSettings { ServerUrl = address, UserId = "user-two", DownloadDirectory = settings.DownloadDirectory }))
        Check(other.GetTracks().Count == 0, "Downloads remain isolated by server and account");
    var migratedRoot = Path.Combine(temp, "migrated");
    await downloads.ChangeDirectoryAsync(migratedRoot);
    Check(downloads.DirectoryPath.StartsWith(migratedRoot) && File.ReadAllBytes(downloads.GetLocalPath(track)).SequenceEqual(bytes), "Switching directory copies existing audio and updates playback lookup");
    Check(File.Exists(file), "Original cached files remain available after changing directory");
    using (var restarted = new DownloadService(new AppSettings { ServerUrl = address, UserId = "user-one", DownloadDirectory = migratedRoot }))
        Check(restarted.GetTracks().Single().Id == track.Id, "Custom cache survives process restart");
    var bad = new Track { Id = "bad", Container = "flac" };
    await Fails(() => downloads.DownloadAsync(bad, address + "/json"), "JSON error responses cannot become cached audio");
    await Fails(() => downloads.DownloadAsync(bad, address + "/upper-json"), "Error-content MIME matching is case-insensitive as required by HTTP");
    await Fails(() => downloads.DownloadAsync(bad, address + "/empty"), "Empty audio response is rejected");
    using (var canceled = new CancellationTokenSource()) { canceled.Cancel(); await Fails(() => downloads.DownloadAsync(bad, address + "/audio", ct: canceled.Token), "Canceled downloads leave no completed entry"); }
    Check(!downloads.IsDownloaded(bad) && !Directory.EnumerateFiles(downloads.DirectoryPath, "*.part").Any(), "Failed downloads leave no partial audio");
    downloads.Remove(track); Check(downloads.GetTracks().Count == 0 && !downloads.IsDownloaded(track), "Removing cached audio updates the download index");
    var interrupted = new Track { Id = "interrupted-index", Title = "Interrupted", Container = "flac" };
    var manifestPath = Path.Combine(downloads.DirectoryPath, "index.json");
    var manifestBefore = File.ReadAllBytes(manifestPath);
    Directory.CreateDirectory(manifestPath + ".tmp");
    await StorageFails(() => downloads.DownloadAsync(interrupted, address + "/audio"), "Index preparation failure rejects the download transaction");
    Check(!downloads.IsDownloaded(interrupted) && File.ReadAllBytes(manifestPath).SequenceEqual(manifestBefore), "Failed index write leaves neither orphan audio nor changes to existing downloads");
    Directory.Delete(manifestPath + ".tmp");
    using (var lockedManifest = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await StorageFails(() => downloads.DownloadAsync(interrupted, address + "/audio"), "Index commit failure rolls back the newly published audio");
    Check(!downloads.IsDownloaded(interrupted) && !File.Exists(manifestPath + ".tmp") && !Directory.EnumerateFiles(downloads.DirectoryPath, "*.part").Any(), "Failed commit cleans partial audio, staged index and completed audio");
    var orphan = new Track { Id = "legacy-orphan", Title = "Legacy orphan", Container = "flac" };
    File.WriteAllBytes(downloads.GetLocalPath(orphan), bytes);
    await downloads.DownloadAsync(orphan, address + "/failure");
    Check(downloads.GetTracks().Single().Id == orphan.Id && File.ReadAllBytes(downloads.GetLocalPath(orphan)).SequenceEqual(bytes), "An old unindexed complete file is repaired without downloading its bytes again");
    using (var cancelAtEnd = new CancellationTokenSource())
    {
        var late = new Track { Id = "late-cancel", Container = "flac" };
        await StorageFails(() => downloads.DownloadAsync(late, address + "/audio", new InlineProgress(value => { if (value >= 100) cancelAtEnd.Cancel(); }), cancelAtEnd.Token), "Cancellation as the final audio block arrives does not commit a download");
        Check(!downloads.IsDownloaded(late) && downloads.GetTracks().All(item => item.Id != late.Id), "Final-block cancellation leaves audio and index consistent");
    }
    var beforeFailedMigration = downloads.DirectoryPath;
    var failedRoot = Path.Combine(temp, "failed-migration");
    var failedDirectory = Path.Combine(failedRoot, Path.GetFileName(downloads.DirectoryPath));
    Directory.CreateDirectory(failedDirectory);
    var failedManifest = Path.Combine(failedDirectory, "index.json"); File.WriteAllText(failedManifest, "[]");
    using (var lockedManifest = new FileStream(failedManifest, FileMode.Open, FileAccess.Read, FileShare.Read))
        await StorageFails(() => downloads.ChangeDirectoryAsync(failedRoot), "Directory migration rejects a destination whose index cannot commit");
    Check(downloads.DirectoryPath == beforeFailedMigration && File.Exists(downloads.GetLocalPath(orphan)) && downloads.GetTracks().Single().Id == orphan.Id, "Failed directory migration preserves the original live playback path and index");
    Check(!File.Exists(failedManifest + ".tmp") && !Directory.EnumerateFiles(failedDirectory, "*.copying").Any(), "Failed migration removes its staged index and partial copies");
    var repairRoot = Path.Combine(temp, "repair-migration"); var repairDirectory = Path.Combine(repairRoot, Path.GetFileName(downloads.DirectoryPath));
    Directory.CreateDirectory(repairDirectory);
    File.WriteAllBytes(Path.Combine(repairDirectory, Path.GetFileName(downloads.GetLocalPath(orphan))), new byte[] { 1 });
    await downloads.ChangeDirectoryAsync(repairRoot);
    Check(File.ReadAllBytes(downloads.GetLocalPath(orphan)).SequenceEqual(bytes), "Migration repairs an existing truncated destination file");
    var concurrentA = new Track { Id = "concurrent-a", Container = "flac" }; var concurrentB = new Track { Id = "concurrent-b", Container = "flac" };
    await Task.WhenAll(downloads.DownloadAsync(concurrentA, address + "/audio"), downloads.DownloadAsync(concurrentB, address + "/audio"));
    Check(downloads.GetTracks().Select(item => item.Id).Order().SequenceEqual(new[] { concurrentA.Id, concurrentB.Id, orphan.Id }.Order()), "Concurrent downloads serialize their commits without losing index entries");
    var whileMigrating = new Track { Id = "while-migrating", Container = "flac" };
    var nextRoot = Path.Combine(temp, "concurrent-migration");
    await Task.WhenAll(downloads.DownloadAsync(whileMigrating, address + "/audio"), downloads.ChangeDirectoryAsync(nextRoot));
    Check(downloads.DirectoryPath.StartsWith(nextRoot) && downloads.GetTracks().Count == 4 && File.ReadAllBytes(downloads.GetLocalPath(whileMigrating)).SequenceEqual(bytes), "Directory migration waits for an active download and keeps its completed entry");
    Console.WriteLine($"All {checks} network/cache checks passed.");
}
finally
{
    stop.Cancel(); http.Stop(); await server;
    if (Directory.Exists(temp)) Directory.Delete(temp, true);
}

sealed class InlineProgress(Action<double> callback) : IProgress<double>
{
    public void Report(double value) => callback(value);
}
