using System.Net;
using System.Net.Sockets;
using System.Text;
using Embistic.Models;
using Embistic.Services;

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}

if (args.Contains("--live"))
{
    var settings = SettingsService.Load();
    Check(settings.ApiKey.Length > 0, "Saved API credential can be decrypted");
    using var client = new EmbyClient(settings);
    var users = await client.GetUsersAsync();
    Check(users.Count > 0, "Emby users fetched");
    var tracks = await client.GetTracksAsync(limit: 12);
    Check(tracks.TotalCount > 0 && tracks.Items.Count > 0, "Emby music paging fetched");
    var albums = await client.GetAlbumsAsync(limit: 3);
    Check(albums.Count > 0, "Emby albums fetched");
    Console.WriteLine($"Music tracks: {tracks.TotalCount}; albums: {await client.GetAlbumCountAsync()}");
    using var player = new PlaybackService();
    var devices = player.GetAudioDevices();
    Check(devices.Count > 1, "Windows audio outputs enumerated");
    foreach (var device in devices) Console.WriteLine("Audio output: " + device.Name);
    player.SetAudioDevice(devices.Last().Id);
    player.SetAudioDevice("");
    player.Volume = 0;
    var errors = new List<string>();
    player.Error += error => errors.Add(error);
    var playbackTrack = tracks.Items.FirstOrDefault(x => x.Container == "flac") ?? tracks.Items[0];
    Console.WriteLine("Playback format: " + playbackTrack.Container);
    player.Play(client.GetStreamUrl(playbackTrack));
    for (var index = 0; index < 40 && player.PositionMs < 1000; index++) await Task.Delay(250);
    Check(errors.Count == 0 && player.IsPlaying && player.PositionMs >= 1000, "Native playback advances from live Emby stream (muted)");
    player.SetAudioDevice(devices.Last().Id);
    await Task.Delay(500);
    Check(player.IsPlaying, "Output device can switch during playback");
    Check(string.Equals(player.CurrentDeviceId, devices.Last().Id, StringComparison.OrdinalIgnoreCase), "Native player confirms selected audio endpoint");
    player.SetAudioDevice("");
    player.Pause();
    await Task.Delay(400);
    Check(!player.IsPlaying, "Pause state");
    player.Seek(30000);
    player.Resume();
    await Task.Delay(1500);
    Check(player.PositionMs > 28000, "Seek and resume");
    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    player.PlaybackEnded += () => ended.TrySetResult();
    player.Seek(player.DurationMs - 1000);
    await ended.Task.WaitAsync(TimeSpan.FromSeconds(8));
    Check(player.HasEnded, "Native EOF callback exposes ended state for queue advancement");
    player.Stop();
    return;
}

var socket = new TcpListener(IPAddress.Loopback, 0);
socket.Start();
var port = ((IPEndPoint)socket.LocalEndpoint).Port;
socket.Stop();
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
var server = Task.Run(async () =>
{
    for (var i = 0; i < 4; i++)
    {
        var context = await listener.GetContextAsync();
        var request = context.Request;
        Check(request.Headers["X-Emby-Token"] == "fixture-token", "Credential sent in API header");
        string json;
        if (request.Url!.AbsolutePath == "/Users") json = "[{\"Id\":\"u1\",\"Name\":\"Listener\"},{\"Id\":\"u2\",\"Name\":\"Disabled\",\"Policy\":{\"IsDisabled\":true}}]";
        else if (request.QueryString["IncludeItemTypes"] == "Audio")
        {
            Check(request.Url.AbsolutePath == "/Users/u1/Items", "User route remains bound to the original connection");
            Check(request.QueryString["StartIndex"] == "200" && request.QueryString["SearchTerm"] == "雨 & 花", "Paging and Unicode search query encoding");
            Check(request.QueryString["Filters"] == "IsFavorite", "Favorites filter");
            json = """{"TotalRecordCount":201,"Items":[{"Id":"42","Name":"雨","Artists":["Singer"],"Album":"Album","AlbumId":"99","RunTimeTicks":2130000000,"IndexNumber":2,"UserData":{"IsFavorite":true},"MediaSources":[{"Id":"source42"}],"MediaStreams":[{"Type":"Audio","Codec":"flac","BitDepth":24,"SampleRate":96000,"BitRate":1200000}]}]}""";
        }
        else if (request.Url.AbsolutePath.EndsWith("/Lyrics")) json = """{"Lyrics":[{"Start":10000000,"Text":"first"},{"Start":203400000,"Text":"second"}]}""";
        else json = """{"TotalRecordCount":2,"Items":[{"Id":"99","Name":"Album","AlbumArtist":"Singer","ProductionYear":2024,"ImageTags":{"Primary":"tag"}}]}""";
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
});
var connectionSettings = new AppSettings { ServerUrl = $"http://127.0.0.1:{port}", ApiKey = "fixture-token", UserId = "u1", UserName = "Listener" };
using (var client = new EmbyClient(connectionSettings))
{
    connectionSettings.ServerUrl = "http://127.0.0.1:1";
    connectionSettings.ApiKey = "different-token";
    connectionSettings.UserId = "different-user";
    connectionSettings.UserName = "Different Listener";
    var users = await client.GetUsersAsync();
    Check(users.Count == 1 && users[0].Name == "Listener", "Disabled users excluded");
    var page = await client.GetTracksAsync("雨 & 花", 200, 1, favoritesOnly: true);
    var track = page.Items[0];
    Check(page.TotalCount == 201 && track.DurationMs == 213000 && track.BitDepth == 24 && track.SampleRate == 96000 && track.IsFavorite, "Track duration, quality and user metadata parsed");
    Check(track.ImageUrl.Contains("/Items/99/Images/Primary"), "Album artwork fallback");
    Check(client.GetStreamUrl(track).Contains("MediaSourceId=source42"), "Direct stream uses source identity");
    var streamUrl = client.GetStreamUrl(track);
    Check(streamUrl.StartsWith($"http://127.0.0.1:{port}/Audio/", StringComparison.Ordinal) && streamUrl.Contains("api_key=fixture-token") && streamUrl.Contains("UserId=u1"), "Settings mutation cannot change the stream server, credential or user");
    Check(client.GetImageUrl(track.AlbumId).Contains("api_key=fixture-token"), "Artwork retains the connection credential snapshot");
    var albums = await client.GetAlbumsAsync(start: 48);
    Check(albums.Count == 1 && albums[0].Artist == "Singer" && albums[0].Year == 2024, "Album metadata parsed");
    Check(await client.GetServerLyricsAsync(track) == "[00:01.00]first\n[00:20.34]second", "Server lyric tick timestamps converted to LRC");
}
await server;
Check(new Track { DurationMs = 3661000 }.DurationText == "1:01:01", "Long duration formatting");
Console.WriteLine("Media service smoke checks completed.");
