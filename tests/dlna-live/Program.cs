using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Embistic.Models;
using Embistic.Services;

// Discovery is read-only. Exercise mode also requires an explicitly selected host.
const string usage = "Usage: DlnaLiveSmoke [--device-host <host>] [--exercise]\n" +
    "Without a target, only discover and list renderers. With a target, read its state.\n" +
    "--exercise (legacy alias: --run) performs the silent test on the selected host.\n" +
    "The target can also be set with EMBISTIC_DLNA_DEVICE_HOST.";
var execute = false;
var targetHost = Environment.GetEnvironmentVariable("EMBISTIC_DLNA_DEVICE_HOST")?.Trim() ?? "";
for (var index = 0; index < args.Length; index++)
{
    if (args[index] is "--help" or "-h") { Console.WriteLine(usage); return 0; }
    if (args[index] is "--exercise" or "--run") { execute = true; continue; }
    if (args[index] == "--device-host" && index + 1 < args.Length)
    {
        targetHost = args[++index].Trim();
        if (targetHost.Length > 0 && !targetHost.StartsWith("--", StringComparison.Ordinal)) continue;
    }
    Console.Error.WriteLine("Invalid or incomplete argument.\n" + usage);
    return 2;
}
targetHost = targetHost.Trim('[', ']').TrimEnd('.');
if (targetHost.Length > 0 && Uri.CheckHostName(targetHost) == UriHostNameType.Unknown)
{
    Console.Error.WriteLine("The device host must be a hostname or IP address, without a URL scheme, port or path.");
    return 2;
}
if (execute && targetHost.Length == 0)
{
    Console.Error.WriteLine("Exercise mode requires --device-host or EMBISTIC_DLNA_DEVICE_HOST. No device was contacted.");
    return 2;
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
using var dlna = new DlnaService();
using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
var checks = new List<string>();
var observations = new List<object>();
var restorationErrors = new List<string>();
var artifacts = Path.GetFullPath(Path.Combine("artifacts", "dlna-live"));
Directory.CreateDirectory(artifacts);
var recoveryPath = Path.Combine(artifacts, "recovery.dpapi");
var resultPath = Path.Combine(artifacts, "result.json");
var entropy = Encoding.UTF8.GetBytes("Embistic.DlnaLiveSmoke.v1");
DlnaDevice? renderer = null;
var originalUri = "";
var originalMetadata = "";
var originalVolume = -1;
bool? originalMute = null;
var originalState = "";
var mutated = false;
var testMediaAttempted = false;
var restored = false;
var outcome = "SKIPPED";
var reason = "";
long initialPosition = 0, advancedPosition = 0, seekPosition = 0, resumedPosition = 0;

string Value(XDocument xml, string name) => xml.Descendants().FirstOrDefault(node => node.Name.LocalName == name)?.Value
    ?? throw new InvalidOperationException("Required state field missing.");
bool IsStopped(string state) => state is "STOPPED" or "NO_MEDIA_PRESENT";
void Check(bool passed, string description)
{
    if (!passed) throw new CheckException(description);
    checks.Add(description);
    Console.WriteLine("PASS: " + description);
}
async Task<XDocument> SoapAsync(Uri uri, string service, string action, IReadOnlyDictionary<string, string> arguments, CancellationToken ct)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, uri);
    request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service}#{action}\"");
    request.Content = new StringContent(DlnaService.BuildSoapEnvelope(service, action, arguments), Encoding.UTF8, "text/xml");
    using var response = await http.SendAsync(request, ct);
    response.EnsureSuccessStatusCode();
    var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    if (xml.Descendants().Any(node => node.Name.LocalName == "Fault")) throw new InvalidOperationException("Speaker SOAP fault.");
    return xml;
}
Task<XDocument> AvAsync(string action, IReadOnlyDictionary<string, string>? arguments, CancellationToken ct) =>
    SoapAsync(renderer!.AvTransportUrl, renderer.AvTransportServiceType, action, arguments ?? new Dictionary<string, string> { ["InstanceID"] = "0" }, ct);
async Task<string> GetStateAsync(CancellationToken ct) => Value(await AvAsync("GetTransportInfo", null, ct), "CurrentTransportState").Trim();
async Task<int> GetVolumeAsync(CancellationToken ct)
{
    var xml = await SoapAsync(renderer!.RenderingControlUrl!, renderer.RenderingControlServiceType, "GetVolume", new Dictionary<string, string> { ["InstanceID"] = "0", ["Channel"] = "Master" }, ct);
    if (!int.TryParse(Value(xml, "CurrentVolume"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume) || volume is < 0 or > 100)
        throw new InvalidOperationException("Speaker volume cannot be preserved.");
    return volume;
}
async Task<bool> AwaitVolumeAsync(int expected, CancellationToken ct)
{
    var observed = -1;
    for (var attempt = 0; attempt < 12; attempt++)
    {
        observed = await GetVolumeAsync(ct);
        if (observed == expected) return true;
        await Task.Delay(150, ct);
    }
    Console.WriteLine($"Volume acknowledgement did not reach {expected}; reported value: {observed}.");
    return false;
}
async Task<bool> GetMuteAsync(CancellationToken ct)
{
    var xml = await SoapAsync(renderer!.RenderingControlUrl!, renderer.RenderingControlServiceType, "GetMute", new Dictionary<string, string> { ["InstanceID"] = "0", ["Channel"] = "Master" }, ct);
    return Value(xml, "CurrentMute").Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => throw new InvalidOperationException("Speaker mute state cannot be preserved.")
    };
}
Task<XDocument> SetMuteAsync(bool mute, CancellationToken ct) =>
    SoapAsync(renderer!.RenderingControlUrl!, renderer.RenderingControlServiceType, "SetMute", new Dictionary<string, string> { ["InstanceID"] = "0", ["Channel"] = "Master", ["DesiredMute"] = mute ? "1" : "0" }, ct);
async Task<bool> AwaitMuteAsync(bool expected, CancellationToken ct)
{
    for (var attempt = 0; attempt < 12; attempt++)
    {
        if (await GetMuteAsync(ct) == expected) return true;
        await Task.Delay(150, ct);
    }
    return false;
}
async Task<DlnaPosition> AwaitStateAsync(string expected, CancellationToken ct)
{
    for (var attempt = 0; attempt < 12; attempt++)
    {
        var position = await dlna.GetPositionAsync(renderer!, ct);
        observations.Add(new { Phase = "await-" + expected, position.State, position.PositionMs, position.DurationMs });
        if (position.State.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || (expected == "PAUSED_PLAYBACK" && position.State.Contains("PAUSED", StringComparison.OrdinalIgnoreCase))) return position;
        await Task.Delay(250, ct);
    }
    throw new CheckException("Speaker did not reach expected transport state: " + expected);
}
async Task<DlnaPosition> AwaitAdvanceAsync(long previous, string phase, CancellationToken ct)
{
    for (var attempt = 0; attempt < 20; attempt++)
    {
        await Task.Delay(500, ct);
        var position = await dlna.GetPositionAsync(renderer!, ct);
        observations.Add(new { Phase = phase, position.State, position.PositionMs, position.DurationMs });
        if (position.State == "PLAYING" && position.PositionMs > previous) return position;
        if (attempt >= 3 && IsStopped(position.State)) throw new CheckException("Speaker stopped before the playback position advanced.");
    }
    throw new CheckException("Speaker position did not advance within the ten-second observation window.");
}

try
{
    var devices = await dlna.DiscoverAsync(deadline.Token);
    var matches = targetHost.Length == 0 ? Array.Empty<DlnaDevice>() : devices.Where(device =>
        device.AvTransportUrl.Host.Trim('[', ']').TrimEnd('.').Equals(targetHost, StringComparison.OrdinalIgnoreCase)).ToArray();
    if (matches.Length == 1) renderer = matches[0];
    if (targetHost.Length == 0)
    {
        foreach (var device in devices) Console.WriteLine($"Renderer: {device.Name}; host: {device.AvTransportUrl.Host}");
        reason = "Read-only discovery completed. Select a listed host with --device-host to inspect its state.";
    }
    else if (matches.Length > 1) { reason = "More than one renderer matched the selected host; no device was changed."; }
    else if (renderer is null) { reason = "The explicitly selected speaker was not discovered."; }
    else if (renderer.RenderingControlUrl is null) { reason = "Target speaker does not expose readable volume control."; }
    else
    {
        originalState = await GetStateAsync(deadline.Token);
        originalVolume = await GetVolumeAsync(deadline.Token);
        originalMute = await GetMuteAsync(deadline.Token);
        var media = await AvAsync("GetMediaInfo", null, deadline.Token);
        originalUri = Value(media, "CurrentURI");
        originalMetadata = Value(media, "CurrentURIMetaData");
        Console.WriteLine($"Speaker: {renderer.Name}; original transport: {originalState}; original volume: {originalVolume}; original mute: {originalMute}; prior media present: {originalUri.Length > 0}");
        if (!IsStopped(originalState)) reason = "Speaker is playing or paused; no state was changed.";
        else if (!execute) reason = "Read-only state check completed. Add --exercise only when ready to run the silent test on this selected host.";
        else
        {
            var settings = SettingsService.Load();
            if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.ServerUrl)) throw new CheckException("Saved Emby credentials unavailable; no state changed.");
            using var emby = new EmbyClient(settings);
            var track = (await emby.GetTracksAsync(limit: 30, cancellationToken: deadline.Token)).Items.FirstOrDefault(item => item.Container.Equals("flac", StringComparison.OrdinalIgnoreCase) && item.DurationMs > 60000)
                ?? throw new CheckException("No suitable FLAC track found; no state changed.");
            if (!IsStopped(await GetStateAsync(deadline.Token)))
                reason = "Speaker started playing while preparing; no state was changed.";
            else
            {
                // Encrypted emergency recovery data, never plaintext credentials or media URLs.
                var recovery = JsonSerializer.SerializeToUtf8Bytes(new { renderer.Id, renderer.Name, renderer.AvTransportUrl, renderer.AvTransportServiceType, renderer.RenderingControlUrl, renderer.RenderingControlServiceType, OriginalUri = originalUri, OriginalMetadata = originalMetadata, OriginalVolume = originalVolume, OriginalMute = originalMute });
                File.WriteAllBytes(recoveryPath, ProtectedData.Protect(recovery, entropy, DataProtectionScope.CurrentUser));
                CryptographicOperations.ZeroMemory(recovery);
                mutated = true;
                await dlna.SetVolumeAsync(renderer, 0, deadline.Token);
                // Some renderers do not honor numeric volume zero. Their standard mute
                // flag must be acknowledged before any test media is loaded or played.
                await SetMuteAsync(true, deadline.Token);
                Check(await AwaitMuteAsync(true, deadline.Token), "Speaker mute is confirmed before loading test audio");
                testMediaAttempted = true;
                await dlna.PlayAsync(renderer, emby.GetStreamUrl(track), track, deadline.Token);
                Check(await GetMuteAsync(deadline.Token), "Speaker remains muted after Play");
                var first = await AwaitStateAsync("PLAYING", deadline.Token);
                initialPosition = first.PositionMs;
                observations.Add(new { Phase = "started", first.State, first.PositionMs, first.DurationMs });
                var later = await AwaitAdvanceAsync(initialPosition, "playing", deadline.Token);
                advancedPosition = later.PositionMs;
                Check(later.State == "PLAYING" && advancedPosition > initialPosition, "Real Emby FLAC plays and its position advances");
                await dlna.PauseAsync(renderer, deadline.Token);
                var paused = await AwaitStateAsync("PAUSED_PLAYBACK", deadline.Token);
                Check(paused.State.Contains("PAUSED", StringComparison.OrdinalIgnoreCase), "Pause is confirmed by the speaker");
                await dlna.ResumeAsync(renderer, deadline.Token);
                var resumed = await AwaitStateAsync("PLAYING", deadline.Token);
                resumedPosition = (await AwaitAdvanceAsync(resumed.PositionMs, "resumed", deadline.Token)).PositionMs;
                Check(resumedPosition > resumed.PositionMs, "Resume restores advancing playback");
                await dlna.SeekAsync(renderer, 30000, deadline.Token);
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    await Task.Delay(250, deadline.Token);
                    var sought = await dlna.GetPositionAsync(renderer, deadline.Token);
                    observations.Add(new { Phase = "seek", sought.State, sought.PositionMs, sought.DurationMs });
                    seekPosition = sought.PositionMs;
                    if (seekPosition >= 29000 && seekPosition < 40000) break;
                }
                Check(seekPosition >= 29000 && seekPosition < 40000, "Seek to 30 seconds is reflected by the speaker");
                var afterSeek = await AwaitAdvanceAsync(seekPosition, "after-seek", deadline.Token);
                Check(afterSeek.PositionMs > seekPosition, "Playback advances after seeking");
                Check(await GetMuteAsync(deadline.Token), "Speaker remained muted throughout the checks");
                await dlna.StopAsync(renderer, deadline.Token);
                Check(IsStopped(await GetStateAsync(deadline.Token)), "Stop is confirmed by the speaker");
                outcome = "PASSED";
            }
        }
    }
}
catch (CheckException ex) { outcome = testMediaAttempted ? "FAILED" : "SKIPPED"; reason = ex.Message; }
catch (Exception ex) { outcome = testMediaAttempted ? "FAILED" : "SKIPPED"; reason = "Operation could not be completed safely (" + ex.GetType().Name + ")."; }
finally
{
    if (mutated && renderer is not null)
    {
        using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = restoreTimeout.Token;
        var quiet = false;
        try { await dlna.StopAsync(renderer, ct); quiet = IsStopped(await GetStateAsync(ct)); }
        catch { restorationErrors.Add("Could not confirm the speaker stopped."); }
        if (quiet)
        {
            try
            {
                await AvAsync("SetAVTransportURI", new Dictionary<string, string> { ["InstanceID"] = "0", ["CurrentURI"] = originalUri, ["CurrentURIMetaData"] = originalMetadata }, ct);
                await dlna.StopAsync(renderer, ct);
                var restoredMedia = await AvAsync("GetMediaInfo", null, ct);
                if (Value(restoredMedia, "CurrentURI") != originalUri || Value(restoredMedia, "CurrentURIMetaData") != originalMetadata)
                    restorationErrors.Add("Previous media URI or metadata did not round-trip exactly.");
            }
            catch { restorationErrors.Add(originalUri.Length > 0 ? "Previous media URI/metadata could not be restored." : "Speaker did not accept clearing the temporary media URI."); }
            try
            {
                quiet = IsStopped(await GetStateAsync(ct));
                if (!quiet) restorationErrors.Add("Speaker is not stopped; original volume was not restored.");
                else
                {
                    await dlna.SetVolumeAsync(renderer, originalVolume, ct);
                    if (!await AwaitVolumeAsync(originalVolume, ct)) restorationErrors.Add("Original volume did not restore exactly.");
                    if (!IsStopped(await GetStateAsync(ct))) restorationErrors.Add("Speaker changed state; original mute was not restored.");
                    else if (originalMute is { } mute)
                    {
                        await SetMuteAsync(mute, ct);
                        if (!await AwaitMuteAsync(mute, ct)) restorationErrors.Add("Original mute state did not restore exactly.");
                    }
                }
            }
            catch { restorationErrors.Add("Could not verify original volume restoration."); }
        }
        if (restorationErrors.Count == 0)
        {
            restored = true;
            File.Delete(recoveryPath);
            Console.WriteLine("RESTORED: original media URI, metadata, volume and mute; speaker is stopped.");
        }
        else
        {
            outcome = "FAILED";
            Console.WriteLine("RESTORATION NEEDS ATTENTION: " + string.Join(" ", restorationErrors));
            Console.WriteLine("Encrypted recovery data retained under artifacts/dlna-live/recovery.dpapi.");
        }
    }
}
var report = new
{
    Outcome = outcome, Reason = reason, Device = renderer?.Name, OriginalState = originalState, OriginalVolume = originalVolume, OriginalMute = originalMute,
    Checks = checks, Observations = observations, InitialPositionMs = initialPosition, AdvancedPositionMs = advancedPosition, SeekPositionMs = seekPosition,
    ResumedPositionMs = resumedPosition, ChangedDevice = mutated, TestMediaAttempted = testMediaAttempted, Restored = restored, RestorationErrors = restorationErrors,
    FinishedAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8))
};
File.WriteAllText(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(outcome + (string.IsNullOrEmpty(reason) ? "" : ": " + reason));
return outcome == "FAILED" ? 1 : 0;

sealed class CheckException(string message) : Exception(message);
