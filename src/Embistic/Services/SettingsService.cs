using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Per-user settings. The API token is encrypted by Windows DPAPI for this account.</summary>
public static class SettingsService
{
    private static readonly object Gate = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Embistic.Settings.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Embistic");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string? LastLoadWarning { get; private set; }

    public static AppSettings Load()
    {
        lock (Gate)
        {
            LastLoadWarning = null;
            if (!File.Exists(SettingsPath)) return new AppSettings();
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
                if (node is null) return new AppSettings();
                var settings = node.Deserialize<AppSettings>() ?? new AppSettings();
                if (node[nameof(AppSettings.Appearance)] is null)
                    settings.Appearance.DesktopFontSize = settings.LyricFontSize;
                // Deliberately never accept a plaintext token from a configuration file.
                settings.ApiKey = "";
                if (node["ApiKeyProtected"]?.GetValue<string>() is { Length: > 0 } encrypted)
                {
                    try { settings.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser)); }
                    catch (Exception ex) when (ex is CryptographicException or FormatException)
                    { LastLoadWarning = "无法解密保存的 API 密钥，请在设置中重新输入。"; }
                }
                Sanitize(settings);
                return settings;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                LastLoadWarning = "无法读取配置，将使用默认设置。";
                return new AppSettings();
            }
        }
    }

    public static void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (Gate)
        {
            Sanitize(settings);
            var node = JsonSerializer.SerializeToNode(settings)!.AsObject();
            node.Remove(nameof(AppSettings.ApiKey));
            node["ApiKeyProtected"] = string.IsNullOrEmpty(settings.ApiKey) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.ApiKey), Entropy, DataProtectionScope.CurrentUser));
            Directory.CreateDirectory(DataDirectory);
            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, node.ToJsonString(JsonOptions), new UTF8Encoding(false));
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
    }

    private static void Sanitize(AppSettings settings)
    {
        settings.ServerUrl = (settings.ServerUrl ?? "").Trim().TrimEnd('/');
        settings.ApiKey = (settings.ApiKey ?? "").Trim();
        settings.UserId ??= "";
        settings.UserName ??= "";
        settings.AudioDeviceId ??= "";
        settings.Volume = Math.Clamp(settings.Volume, 0, 100);
        settings.LyricsOffsetMs = double.IsFinite(settings.LyricsOffsetMs) ? Math.Clamp(settings.LyricsOffsetMs, -600000, 600000) : 0;
        settings.LyricFontSize = double.IsFinite(settings.LyricFontSize) ? Math.Clamp(settings.LyricFontSize, 16, 80) : 32;
        if (!double.IsFinite(settings.LyricsLeft)) settings.LyricsLeft = 300;
        if (!double.IsFinite(settings.LyricsTop)) settings.LyricsTop = 720;
        settings.Appearance ??= new LyricAppearanceSettings();
        settings.Appearance.Normalize();
        settings.Theme ??= new ThemeSettings();
        settings.Theme.Normalize();
        settings.Spectrum ??= new SpectrumSettings();
        settings.Spectrum.Normalize();
        settings.LoginMode = settings.LoginMode == "Password" ? "Password" : "ApiKey";
        settings.Display ??= new DisplaySettings();
        settings.Display.Normalize();
        settings.DownloadDirectory = (settings.DownloadDirectory ?? "").Trim();
        if (settings.DownloadDirectory.Length > 0)
        {
            try { settings.DownloadDirectory = Path.GetFullPath(settings.DownloadDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { settings.DownloadDirectory = ""; }
        }
        settings.LyricsSources = (settings.LyricsSources ?? new()).Where(source => source is not null).Take(16).ToList();
        foreach (var source in settings.LyricsSources) source.Normalize();
    }
}
