using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Embistic;

public sealed class ArtworkConverter : IValueConverter
{
    private readonly Dictionary<string, WeakReference<BitmapImage>> _cache = new();
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (_cache.TryGetValue(url, out var weak) && weak.TryGetTarget(out var image)) return image;
        try
        {
            image = new BitmapImage();
            image.BeginInit();
            image.DecodePixelWidth = 440;
            image.CacheOption = BitmapCacheOption.OnDemand;
            image.UriSource = uri;
            image.EndInit();
            _cache[url] = new WeakReference<BitmapImage>(image);
            if (_cache.Count > 1500) _cache.Clear();
            return image;
        }
        catch { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
