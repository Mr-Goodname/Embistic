using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Embistic.Models;
using Embistic.Services;

var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    passed++;
    Console.WriteLine("PASS: " + name);
}

if (args.Contains("--discover"))
{
    using var scanner = new DlnaService();
    var stopwatch = Stopwatch.StartNew();
    var found = await scanner.DiscoverAsync();
    Console.WriteLine($"Discovery completed in {stopwatch.Elapsed.TotalSeconds:0.00}s; renderers: {found.Count}");
    foreach (var renderer in found) Console.WriteLine($"{renderer.Name} | {renderer.AvTransportServiceType} | {renderer.AvTransportUrl.Host}");
    return;
}

Check(DlnaService.ParseSsdpLocation("HTTP/1.1 200 OK\r\nLoCaTiOn: http://192.168.1.5:8000/device.xml\r\n\r\n")?.Host == "192.168.1.5", "SSDP case-insensitive header parsing");
Check(DlnaService.ParseSsdpLocation("NOTIFY * HTTP/1.1\r\nLOCATION: http://localhost/device.xml\r\n") is null, "SSDP ignores non-search responses");
Check(DlnaService.ParseSsdpLocation("HTTP/1.1 200 OK\r\nLOCATION: file:///tmp/test\r\n") is null, "SSDP rejects non-HTTP locations");
Check(DlnaService.ParseSsdpLocation("HTTP/1.1 200 OK\r\nLOCATION: http://user:password@localhost/device.xml\r\n") is null, "SSDP rejects embedded credentials");
var description = XDocument.Parse("""
    <root xmlns="urn:schemas-upnp-org:device-1-0"><URLBase>http://192.168.2.50:8888/base/</URLBase>
    <device><friendlyName>Parent router</friendlyName><UDN>uuid:parent</UDN><deviceList>
    <device><friendlyName>书房 &amp; 音响</friendlyName><UDN>uuid:speaker</UDN><serviceList>
    <service><serviceType>urn:schemas-upnp-org:service:AVTransport:2</serviceType><controlURL>control/av</controlURL></service>
    <service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType><controlURL>/control/volume</controlURL></service>
    </serviceList></device></deviceList></device></root>
    """);
var parsed = DlnaService.ParseDeviceDescription(description, new Uri("http://192.168.2.50/device.xml"));
Check(parsed.Count == 1 && parsed[0].Name == "书房 & 音响" && parsed[0].Id == "uuid:speaker", "Embedded renderer is associated with its own services");
Check(parsed[0].AvTransportUrl.AbsoluteUri == "http://192.168.2.50:8888/base/control/av" && parsed[0].RenderingControlUrl?.AbsoluteUri == "http://192.168.2.50:8888/control/volume", "URLBase and relative control URL resolution");
Check(parsed[0].AvTransportServiceType.EndsWith(":2"), "Renderer service version preserved");
var fallback = XDocument.Parse("<root><device><serviceList><service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><controlURL>../av</controlURL></service></serviceList></device></root>");
Check(DlnaService.ParseDeviceDescription(fallback, new Uri("http://localhost:1234/upnp/device.xml"))[0].AvTransportUrl.AbsoluteUri == "http://localhost:1234/av", "Description URL fallback resolution");

var track = new Track { Id = "id<&\"", Title = "曲目 <test> & \"声\"", Artist = "A & B", Album = "专辑", DurationMs = 25 * 3600000L + 1234, Container = "flac", BitRate = 800000, SampleRate = 96000, ImageUrl = "http://localhost/image?id=5&key=example" };
const string streamUrl = "http://localhost/audio.flac?id=5&api_key=example";
var metadata = DlnaService.BuildDidlMetadata(streamUrl, track);
var didl = XDocument.Parse(metadata);
Check(didl.Descendants().Single(e => e.Name.LocalName == "title").Value == track.Title, "DIDL metadata XML escaping round trip");
var res = didl.Descendants().Single(e => e.Name.LocalName == "res");
Check(res.Value == streamUrl && res.Attribute("protocolInfo")?.Value == "http-get:*:audio/flac:*", "DIDL resource URL and FLAC MIME");
Check(res.Attribute("duration")?.Value == "25:00:01.234" && res.Attribute("bitrate")?.Value == "100000", "DIDL duration beyond 24 hours and bitrate byte units");
var envelope = XDocument.Parse(DlnaService.BuildSoapEnvelope(parsed[0].AvTransportServiceType, "SetAVTransportURI", new Dictionary<string,string> { ["CurrentURI"] = streamUrl, ["CurrentURIMetaData"] = metadata }));
Check(envelope.Descendants().Single(e => e.Name.LocalName == "CurrentURIMetaData").Value == metadata, "SOAP nested DIDL correctly escaped once");
Check(envelope.Descendants().Single(e => e.Name.LocalName == "SetAVTransportURI").Name.NamespaceName.EndsWith("AVTransport:2"), "SOAP action uses advertised version");
Check(DlnaService.ParseTime("25:01:02.350") == 90062350 && DlnaService.FormatTime(90062350, false) == "25:01:02", "UPnP time supports extended hours and fractions");
Check(new[] { null, "NOT_IMPLEMENTED", "-01:02:03", "1:60:01", "1:00:60", "hello", "9223372036854775807:00:00" }.All(s => DlnaService.ParseTime(s) == 0), "Malformed and overflowing times rejected");

var reservation = new TcpListener(IPAddress.Loopback, 0);
reservation.Start();
var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
reservation.Stop();
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
var requests = new List<(string Action, XDocument Body)>();
var fakeServer = Task.Run(async () =>
{
    for (var i = 0; i < 11; i++)
    {
        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(15));
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = XDocument.Parse(await reader.ReadToEndAsync());
        var action = context.Request.Headers["SOAPACTION"]?.Trim('"').Split('#').Last() ?? "";
        requests.Add((action, body));
        string payload;
        if (i == 9)
        {
            context.Response.StatusCode = 500;
            payload = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><detail><UPnPError><errorCode>714</errorCode><errorDescription>api_key=secret-never-report</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        }
        else if (i == 10)
        {
            context.Response.StatusCode = 503;
            payload = "not XML api_key=secret-never-report";
        }
        else
        {
            var values = action switch
            {
                "GetPositionInfo" => "<TrackDuration>00:05:20.500</TrackDuration><RelTime>00:01:12.300</RelTime>",
                "GetTransportInfo" => "<CurrentTransportState>PLAYING</CurrentTransportState>",
                _ => ""
            };
            payload = $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:{action}Response xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:2\">{values}</u:{action}Response></s:Body></s:Envelope>";
        }
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.ContentType = "text/xml; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
});
using (var service = new DlnaService())
{
    var fake = new DlnaDevice("uuid:test", "Test renderer", new Uri($"http://127.0.0.1:{port}/av"), new Uri($"http://127.0.0.1:{port}/rc")) { AvTransportServiceType = "urn:schemas-upnp-org:service:AVTransport:2" };
    await service.PlayAsync(fake, streamUrl, track);
    await service.PauseAsync(fake);
    await service.ResumeAsync(fake);
    await service.SeekAsync(fake, 100500);
    await service.SetVolumeAsync(fake, 150);
    var position = await service.GetPositionAsync(fake);
    await service.StopAsync(fake);
    Check(position == new DlnaPosition(72300, 320500, "PLAYING"), "HTTP renderer position and transport state responses");
    try { await service.ResumeAsync(fake); throw new Exception("Expected UPnP fault"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("不支持") && !ex.ToString().Contains("secret-never-report"), "UPnP fault translated without exposing response secrets"); }
    try { await service.ResumeAsync(fake); throw new Exception("Expected HTTP failure"); }
    catch (InvalidOperationException ex) { Check(ex.Message.Contains("503") && !ex.ToString().Contains("secret-never-report"), "Non-XML HTTP failure translated without response secrets"); }
}
await fakeServer;
Check(requests.Take(9).Select(r => r.Action).SequenceEqual(new[] { "SetAVTransportURI", "Play", "Pause", "Play", "Seek", "SetVolume", "GetPositionInfo", "GetTransportInfo", "Stop" }), "Actual SOAP request sequence for all transport operations");
Check(requests[0].Body.Descendants().Single(e => e.Name.LocalName == "CurrentURI").Value == streamUrl, "Actual SOAP preserves authenticated media URL");
Check(requests[4].Body.Descendants().Single(e => e.Name.LocalName == "Target").Value == "00:01:40", "Actual seek formats UPnP time");
Check(requests[5].Body.Descendants().Single(e => e.Name.LocalName == "DesiredVolume").Value == "100", "Actual volume command clamps to supported range");
Console.WriteLine($"Completed: {passed} DLNA checks passed.");
