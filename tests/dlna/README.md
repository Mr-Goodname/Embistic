# DLNA checks

Run with a working .NET 10 SDK:

```powershell
dotnet run --project tests/dlna/DlnaChecks.csproj
dotnet run --project tests/dlna/DlnaChecks.csproj -- --discover
```

The default mode runs 22 checks without contacting a physical renderer. It exercises SSDP parsing, embedded-device descriptions, relative service URLs, service version selection, DIDL/SOAP escaping, extended UPnP time values, and a loopback HTTP renderer implementing all transport operations and error responses.

`--discover` only sends SSDP discovery packets and reads device descriptions. It does not play audio or alter device volume.

Physical audio playback is not established by discovery. Supported audio formats depend on the renderer; the service sends the authenticated Emby stream URL directly to it. Optional tests against an explicitly selected physical device are documented in [the live smoke test](../dlna-live/README.md).
