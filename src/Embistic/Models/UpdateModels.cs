namespace Embistic.Models;

public sealed record UpdateInfo(
    string Version,
    string ReleaseNotes,
    string PageUrl,
    string? InstallerUrl,
    string? ZipUrl,
    bool IsNewer);
