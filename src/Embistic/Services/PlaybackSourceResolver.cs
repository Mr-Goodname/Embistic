using System.IO;
using Embistic.Models;

namespace Embistic.Services;

/// <summary>Selects a playable source without sending local paths to a remote renderer.</summary>
public static class PlaybackSourceResolver
{
    public static bool IsRadio(Track track) => track.Id.StartsWith("radio:", StringComparison.Ordinal);

    public static string GetUrl(Track track, string? downloadedPath, bool remote, EmbyClient? client)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (IsRadio(track))
        {
            if (!Uri.TryCreate(track.Path, UriKind.Absolute, out var station) || station.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(station.UserInfo))
                throw new InvalidOperationException("网络电台地址无效，请重新添加电台。");
            return station.AbsoluteUri;
        }
        if (!remote && !string.IsNullOrWhiteSpace(downloadedPath) && File.Exists(downloadedPath))
            return new Uri(Path.GetFullPath(downloadedPath)).AbsoluteUri;
        if (client is null)
            throw new InvalidOperationException(remote ? "投放 Emby 歌曲需要连接服务器，音箱会直接读取服务器音频。" : "这首歌曲尚未下载，请先连接 Emby。");
        return client.GetStreamUrl(track);
    }
}
