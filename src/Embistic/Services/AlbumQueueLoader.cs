using System.IO;
using Embistic.Models;

namespace Embistic.Services;

public static class AlbumQueueLoader
{
    public static async Task<IReadOnlyList<Track>> LoadAsync(Func<int, int, CancellationToken, Task<PagedResult<Track>>> fetch, CancellationToken cancellationToken = default)
    {
        var tracks = new List<Track>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var offset = 0; var total = int.MaxValue;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await fetch(offset, 1000, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            total = Math.Max(page.TotalCount, offset + page.Items.Count);
            if (total > 100000) throw new InvalidDataException("专辑曲目数超过支持范围。");
            if (page.Items.Count == 0) break;
            var added = 0;
            foreach (var track in page.Items) if (seen.Add(track.Id)) { tracks.Add(track); added++; }
            if (added == 0) throw new InvalidDataException("服务器重复返回同一页曲目，未更改播放队列。");
            offset += page.Items.Count;
        } while (offset < total);
        if (offset < total) throw new InvalidDataException("专辑曲目未完整返回，未更改播放队列。");
        return tracks;
    }
}
