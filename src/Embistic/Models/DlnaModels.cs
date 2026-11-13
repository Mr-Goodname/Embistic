namespace Embistic.Models;

public sealed record DlnaDevice(string Id, string Name, Uri AvTransportUrl, Uri? RenderingControlUrl)
{
    public string AvTransportServiceType { get; init; } = "urn:schemas-upnp-org:service:AVTransport:1";
    public string RenderingControlServiceType { get; init; } = "urn:schemas-upnp-org:service:RenderingControl:1";
    public override string ToString() => Name;
}

public sealed record DlnaPosition(long PositionMs, long DurationMs, string State);
