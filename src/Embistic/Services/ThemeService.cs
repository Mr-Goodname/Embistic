using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Embistic.Models;
using Microsoft.Win32;

namespace Embistic.Services;

/// <summary>Applies semantic WPF resources and observes Windows preferences without changing them.</summary>
public sealed class ThemeService : IDisposable
{
    private ThemeSettings _settings = new();
    private bool _disposed;
    public event Action? ThemeChanged;
    public bool IsDark { get; private set; } = true;
    public Color Accent { get; private set; } = Parse("#ADC9F0");

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        SystemParameters.StaticPropertyChanged += SystemParameterChanged;
    }

    public void Apply(ThemeSettings settings)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Clone();
        _settings.Normalize();
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => Apply(_settings)); return; }
        IsDark = _settings.Mode == "Dark" || (_settings.Mode == "System" && ReadSystemDarkMode());
        Accent = _settings.FollowSystemAccent ? ReadSystemAccent() : Parse(_settings.AccentColor);
        var palette = CreatePalette(IsDark, Accent);
        foreach (var (key, color) in palette)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            app.Resources[key] = brush;
        }
        foreach (Window window in app.Windows) ApplyWindowChrome(window);
        ThemeChanged?.Invoke();
    }

    public void ApplyWindowChrome(Window window)
    {
        if (_disposed) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var dark = IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            var bg = Parse(IsDark ? "#0C1015" : "#F4F7FC");
            var fg = Parse(IsDark ? "#EDF2FA" : "#182334");
            var caption = ColorRef(bg);
            var text = ColorRef(fg);
            _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => RefreshSystemPreferences();
    private void SystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.WindowGlassColor) or nameof(SystemParameters.HighContrast))
            RefreshSystemPreferences();
    }
    private void RefreshSystemPreferences()
    {
        if (_disposed || (!_settings.FollowSystemAccent && _settings.Mode != "System")) return;
        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted) return;
        _ = app.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) Apply(_settings); }));
    }

    public static bool ReadSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { return false; }
    }

    public static Color ReadSystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("ColorizationColor") is int value)
            {
                var argb = unchecked((uint)value);
                return Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            }
            var color = SystemParameters.WindowGlassColor;
            return Color.FromRgb(color.R, color.G, color.B);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        { return Parse("#0078D4"); }
    }

    public static IReadOnlyDictionary<string, Color> CreatePalette(bool dark, Color accent)
    {
        accent.A = 255;
        var bg = Parse(dark ? "#0C1015" : "#F4F7FC");
        var panel = Parse(dark ? "#131920" : "#FFFFFF");
        var text = Parse(dark ? "#EDF2FA" : "#182334");
        return new Dictionary<string, Color>
        {
            ["Bg"] = bg, ["Panel"] = panel,
            ["Raised"] = Parse(dark ? "#1B2430" : "#E8EEF7"),
            ["Line"] = Parse(dark ? "#27313E" : "#D4DCE8"),
            ["Accent"] = accent,
            ["AccentText"] = Contrast(accent, Colors.White) >= Contrast(accent, Parse("#101720")) ? Colors.White : Parse("#101720"),
            ["AccentInk"] = ReadableAccent(accent, bg, dark),
            ["Text"] = text, ["Muted"] = Parse(dark ? "#8F9BAE" : "#5D6B7E"),
            ["Sidebar"] = Parse(dark ? "#10161E" : "#E9EFF7"),
            ["Player"] = Parse(dark ? "#151E2A" : "#FFFFFF"),
            ["SecondaryText"] = Parse(dark ? "#B4C3D8" : "#3B4F69"),
            ["Subtle"] = Parse(dark ? "#65758C" : "#79889B"),
            ["AccentSurface"] = Blend(panel, accent, dark ? .14 : .19),
            ["Hover"] = Blend(panel, accent, dark ? .10 : .12),
            ["Selection"] = Blend(panel, accent, dark ? .23 : .25),
            ["Track"] = Parse(dark ? "#384555" : "#C6D1E0"),
            ["Thumb"] = Blend(accent, text, .25),
            ["Danger"] = Parse(dark ? "#F49DA5" : "#B42339"),
        };
    }

    private static Color ReadableAccent(Color accent, Color background, bool dark)
    {
        var result = accent;
        for (var i = 0; i < 12 && Contrast(result, background) < 3; i++)
            result = Blend(result, dark ? Colors.White : Parse("#10223C"), .18);
        return result;
    }
    public static double Contrast(Color first, Color second)
    {
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static double Luminance(Color color)
    {
        static double Channel(byte value) { var c = value / 255.0; return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4); }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
    private static Color Blend(Color a, Color b, double factor) => Color.FromRgb(
        (byte)Math.Round(a.R * (1 - factor) + b.R * factor),
        (byte)Math.Round(a.G * (1 - factor) + b.G * factor),
        (byte)Math.Round(a.B * (1 - factor) + b.B * factor));
    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static int ColorRef(Color color) => color.R | color.G << 8 | color.B << 16;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameterChanged;
    }
}
