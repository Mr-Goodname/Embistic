using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>SSDP discovery and UPnP AVTransport/RenderingControl, without a background multicast listener.</summary>
public sealed class DlnaService : IDisposable
{
    private const int MaximumXmlBytes = 1_048_576;
    private static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly XNamespace SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
    private readonly HttpClient _http;
    private bool _disposed;

    public DlnaService()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3)
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Embistic/0.1 UPnP/1.0");
    }

    /// <summary>Search all currently active IPv4 adapters. The complete scan has a five-second deadline.</summary>
    public async Task<IReadOnlyList<DlnaDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var scan = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        scan.CancelAfter(TimeSpan.FromMilliseconds(2800));
        var locations = new ConcurrentDictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        var interfaces = GetLocalAddresses();
        await Task.WhenAll(interfaces.Select(address => SearchInterfaceAsync(address, locations, scan.Token))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var devices = new ConcurrentDictionary<string, DlnaDevice>(StringComparer.OrdinalIgnoreCase);
        using var parallelism = new SemaphoreSlim(8);
        var requests = locations.Values.Take(128).Select(async location =>
        {
            try
            {
                await parallelism.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    var description = await GetXmlAsync(location, deadline.Token).ConfigureAwait(false);
                    foreach (var device in ParseDeviceDescription(description, location))
                        devices.TryAdd(device.Id, device);
                }
                finally { parallelism.Release(); }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or XmlException or OperationCanceledException or InvalidOperationException)
            {
                // A broken or unreachable advertisement must not prevent the other renderers from appearing.
            }
        });
        await Task.WhenAll(requests).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return devices.Values.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task PlayAsync(DlnaDevice device, string streamUrl, Track track, CancellationToken cancellationToken = default)
    {
        if (!TryHttpUri(streamUrl, out _)) throw new ArgumentException("投放地址必须是 HTTP 或 HTTPS 音频地址。", nameof(streamUrl));
        await SendAvAsync(device, "SetAVTransportURI", new Dictionary<string, string>
        {
            ["InstanceID"] = "0",
            ["CurrentURI"] = streamUrl,
            ["CurrentURIMetaData"] = BuildDidlMetadata(streamUrl, track)
        }, cancellationToken).ConfigureAwait(false);
        await ResumeAsync(device, cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync(DlnaDevice device, CancellationToken cancellationToken = default) =>
        _ = await SendAvAsync(device, "Pause", InstanceArguments(), cancellationToken).ConfigureAwait(false);

    public async Task ResumeAsync(DlnaDevice device, CancellationToken cancellationToken = default) =>
        _ = await SendAvAsync(device, "Play", new Dictionary<string, string> { ["InstanceID"] = "0", ["Speed"] = "1" }, cancellationToken).ConfigureAwait(false);

    public async Task StopAsync(DlnaDevice device, CancellationToken cancellationToken = default) =>
        _ = await SendAvAsync(device, "Stop", InstanceArguments(), cancellationToken).ConfigureAwait(false);

    public async Task SeekAsync(DlnaDevice device, long positionMs, CancellationToken cancellationToken = default) =>
        _ = await SendAvAsync(device, "Seek", new Dictionary<string, string>
        {
            ["InstanceID"] = "0", ["Unit"] = "REL_TIME", ["Target"] = FormatTime(Math.Max(0, positionMs), false)
        }, cancellationToken).ConfigureAwait(false);

    public async Task SetVolumeAsync(DlnaDevice device, int volume, CancellationToken cancellationToken = default)
    {
        if (device.RenderingControlUrl is null) throw new InvalidOperationException("这个 DLNA 设备没有提供音量控制。请使用设备自身的音量键。");
        await SendSoapAsync(device.RenderingControlUrl, device.RenderingControlServiceType, "SetVolume", new Dictionary<string, string>
        {
            ["InstanceID"] = "0", ["Channel"] = "Master", ["DesiredVolume"] = Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture)
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DlnaPosition> GetPositionAsync(DlnaDevice device, CancellationToken cancellationToken = default)
    {
        var position = await SendAvAsync(device, "GetPositionInfo", InstanceArguments(), cancellationToken).ConfigureAwait(false);
        var transport = await SendAvAsync(device, "GetTransportInfo", InstanceArguments(), cancellationToken).ConfigureAwait(false);
        return new DlnaPosition(ParseTime(FindValue(position, "RelTime")), ParseTime(FindValue(position, "TrackDuration")),
            FindValue(transport, "CurrentTransportState") ?? "UNKNOWN");
    }

    private Task<XDocument> SendAvAsync(DlnaDevice device, string action, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken) =>
        SendSoapAsync(device.AvTransportUrl, device.AvTransportServiceType, action, arguments, cancellationToken);

    private async Task<XDocument> SendSoapAsync(Uri address, string serviceType, string action, IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, address);
            request.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{serviceType}#{action}\"");
            request.Content = new StringContent(BuildSoapEnvelope(serviceType, action, arguments), Encoding.UTF8, "text/xml");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            XDocument? xml = null;
            try { xml = await ReadXmlAsync(response.Content, timeout.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException) { }
            var fault = xml?.Descendants().FirstOrDefault(element => element.Name.LocalName == "Fault");
            if (fault is not null)
            {
                var code = FindValue(xml!, "errorCode");
                throw new InvalidOperationException(DescribeFault(code));
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"DLNA 设备拒绝了操作（HTTP {(int)response.StatusCode}）。");
            // Some renderers return an empty 200 response for successful write operations.
            return xml ?? new XDocument(new XElement("Response"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("DLNA 设备响应超时，请检查设备是否在线。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("无法连接 DLNA 设备，请检查它与电脑是否在同一局域网。");
        }
        catch (IOException)
        {
            throw new InvalidOperationException("DLNA 设备中断了连接，请稍后重试。");
        }
    }

    private async Task<XDocument> GetXmlAsync(Uri address, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadXmlAsync(response.Content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<XDocument> ReadXmlAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumXmlBytes) throw new InvalidOperationException("DLNA 设备响应超过大小限制。");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumXmlBytes) throw new InvalidOperationException("DLNA 设备响应超过大小限制。");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumXmlBytes, Async = true
        });
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private static IPAddress[] GetLocalAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Select(address => address.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any))
                .Distinct().ToArray();
            return addresses.Length > 0 ? addresses : [IPAddress.Any];
        }
        catch (NetworkInformationException) { return [IPAddress.Any]; }
    }

    private static async Task SearchInterfaceAsync(IPAddress address, ConcurrentDictionary<string, Uri> locations, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new UdpClient(new IPEndPoint(address, 0));
            if (!address.Equals(IPAddress.Any)) client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
            client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
            foreach (var target in new[] { "urn:schemas-upnp-org:device:MediaRenderer:1", "urn:schemas-upnp-org:service:AVTransport:1" })
            {
                var request = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {target}\r\n\r\n");
                await client.SendAsync(request, SsdpEndpoint, cancellationToken).ConfigureAwait(false);
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (result.Buffer.Length > 16384) continue;
                var location = ParseSsdpLocation(Encoding.ASCII.GetString(result.Buffer));
                if (location is not null && locations.Count < 128) locations.TryAdd(location.AbsoluteUri, location);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or NetworkInformationException or ObjectDisposedException)
        {
            // VPNs and disconnected adapters frequently reject multicast. Other interfaces still complete their scan.
        }
    }

    internal static Uri? ParseSsdpLocation(string response)
    {
        var lines = response.Split('\n');
        if (lines.Length == 0 || !lines[0].TrimEnd().StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)
                && TryHttpUri(line[(colon + 1)..].Trim(), out var location)) return location;
        }
        return null;
    }

    internal static IReadOnlyList<DlnaDevice> ParseDeviceDescription(XDocument description, Uri location)
    {
        var result = new List<DlnaDevice>();
        var declaredBase = description.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "URLBase")?.Value.Trim();
        var baseUri = TryHttpUri(declaredBase, out var explicitBase) ? explicitBase! : location;
        foreach (var device in description.Descendants().Where(element => element.Name.LocalName == "device"))
        {
            var services = device.Elements().FirstOrDefault(element => element.Name.LocalName == "serviceList")?.Elements()
                .Where(element => element.Name.LocalName == "service").ToArray() ?? [];
            var av = services.FirstOrDefault(service => ChildValue(service, "serviceType").StartsWith("urn:schemas-upnp-org:service:AVTransport:", StringComparison.OrdinalIgnoreCase));
            if (av is null || !TryServiceUri(baseUri, ChildValue(av, "controlURL"), out var avUrl)) continue;
            var rc = services.FirstOrDefault(service => ChildValue(service, "serviceType").StartsWith("urn:schemas-upnp-org:service:RenderingControl:", StringComparison.OrdinalIgnoreCase));
            Uri? rcUrl = null;
            if (rc is not null) TryServiceUri(baseUri, ChildValue(rc, "controlURL"), out rcUrl);
            var id = ChildValue(device, "UDN");
            var name = ChildValue(device, "friendlyName");
            result.Add(new DlnaDevice(string.IsNullOrWhiteSpace(id) ? avUrl!.AbsoluteUri : id, string.IsNullOrWhiteSpace(name) ? location.Host : name, avUrl!, rcUrl)
            {
                AvTransportServiceType = ChildValue(av, "serviceType"),
                RenderingControlServiceType = rc is not null ? ChildValue(rc, "serviceType") : "urn:schemas-upnp-org:service:RenderingControl:1"
            });
        }
        return result;
    }

    internal static string BuildSoapEnvelope(string serviceType, string action, IReadOnlyDictionary<string, string> arguments)
    {
        XNamespace service = serviceType;
        return new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(SoapNamespace + "Envelope", new XAttribute(XNamespace.Xmlns + "s", SoapNamespace),
                new XAttribute(SoapNamespace + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(SoapNamespace + "Body", new XElement(service + action,
                    new XAttribute(XNamespace.Xmlns + "u", serviceType), arguments.Select(pair => new XElement(pair.Key, pair.Value))))))
            .ToString(SaveOptions.DisableFormatting);
    }

    internal static string BuildDidlMetadata(string streamUrl, Track track)
    {
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
        var resource = new XElement(didl + "res", new XAttribute("protocolInfo", $"http-get:*:{MimeType(track.Container)}:*"), streamUrl);
        if (track.DurationMs > 0) resource.Add(new XAttribute("duration", FormatTime(track.DurationMs, true)));
        if (track.SampleRate is > 0) resource.Add(new XAttribute("sampleFrequency", track.SampleRate));
        if (track.BitRate is > 0) resource.Add(new XAttribute("bitrate", track.BitRate / 8));
        var item = new XElement(didl + "item", new XAttribute("id", track.Id), new XAttribute("parentID", "0"), new XAttribute("restricted", "1"),
            new XElement(dc + "title", track.Title), new XElement(dc + "creator", track.Artist),
            new XElement(upnp + "artist", track.Artist), new XElement(upnp + "album", track.Album),
            new XElement(upnp + "class", "object.item.audioItem.musicTrack"), resource);
        if (TryHttpUri(track.ImageUrl, out _)) item.Add(new XElement(upnp + "albumArtURI", track.ImageUrl));
        return new XElement(didl + "DIDL-Lite", new XAttribute(XNamespace.Xmlns + "dc", dc), new XAttribute(XNamespace.Xmlns + "upnp", upnp), item)
            .ToString(SaveOptions.DisableFormatting);
    }

    internal static long ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Trim().Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !decimal.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || hours < 0 || minutes is < 0 or > 59 || seconds is < 0 or >= 60) return 0;
        var total = hours * 3600000m + minutes * 60000m + seconds * 1000m;
        return total <= long.MaxValue ? (long)total : 0;
    }

    internal static string FormatTime(long milliseconds, bool fractional)
    {
        milliseconds = Math.Max(0, milliseconds);
        var value = $"{milliseconds / 3600000:00}:{milliseconds / 60000 % 60:00}:{milliseconds / 1000 % 60:00}";
        return fractional ? value + "." + (milliseconds % 1000).ToString("000", CultureInfo.InvariantCulture) : value;
    }

    private static string MimeType(string? container) => container?.ToLowerInvariant().TrimStart('.') switch
    {
        "mp3" => "audio/mpeg", "flac" => "audio/flac", "aac" => "audio/aac", "m4a" or "mp4" or "alac" => "audio/mp4",
        "wav" or "wave" => "audio/wav", "ogg" or "opus" => "audio/ogg", "wma" or "asf" => "audio/x-ms-wma",
        "aiff" or "aif" => "audio/aiff", "ape" => "audio/x-ape", "dsf" => "audio/x-dsf", "dff" => "audio/x-dff",
        _ => "application/octet-stream"
    };

    private static bool TryHttpUri(string? text, out Uri? result)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out result) && result.Scheme is "http" or "https" && string.IsNullOrEmpty(result.UserInfo)) return true;
        result = null;
        return false;
    }

    private static bool TryServiceUri(Uri baseUri, string text, out Uri? result)
    {
        if (!string.IsNullOrWhiteSpace(text) && Uri.TryCreate(baseUri, text, out var resolved) && TryHttpUri(resolved.AbsoluteUri, out result)) return true;
        result = null;
        return false;
    }

    private static string ChildValue(XElement element, string name) => element.Elements().FirstOrDefault(child => child.Name.LocalName == name)?.Value.Trim() ?? "";
    private static string? FindValue(XDocument document, string name) => document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
    private static Dictionary<string, string> InstanceArguments() => new() { ["InstanceID"] = "0" };
    private static string DescribeFault(string? code) => code switch
    {
        "401" => "这个 DLNA 设备不支持此操作（401）。",
        "402" => "DLNA 设备不接受此操作的参数（402）。",
        "701" => "DLNA 设备当前无法切换播放状态（701）。",
        "703" or "716" => "DLNA 设备无法读取音频地址，请确认设备能访问 Emby 服务器。",
        "704" or "714" => "DLNA 设备不支持这首歌的音频格式，请尝试其他格式。",
        "710" or "711" => "这个 DLNA 设备不支持跳转到此播放位置。",
        "715" => "DLNA 设备无法读取受保护的音频资源（715）。",
        "718" => "DLNA 设备无法使用当前播放实例（718）。",
        _ => int.TryParse(code, out var numeric) ? $"DLNA 设备操作失败（UPnP {numeric}）。" : "DLNA 设备操作失败，请检查设备状态。"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
