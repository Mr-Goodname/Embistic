using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; }

    [STAThread]
    private static void Main(string[] args)
    {
        var original = new LyricAppearanceSettings();
        var copy = original.Clone();
        copy.MainFontSize = 500;
        copy.Normalize();
        Check(original.MainFontSize == 26 && copy.MainFontSize == 64, "Editable clone remains isolated");
        var invalid = new LyricAppearanceSettings
        {
            FontFamily = "file:///secret/font.ttf", MainFontSize = double.NaN, DesktopFontSize = double.PositiveInfinity,
            MainFontWeight = -1, DesktopFontWeight = 9999, SecondaryScale = -.5, BackgroundOpacity = 5,
            CurrentColor = "not a color", InactiveColor = "#af3", TranslationColor = "#80aabbcc",
            ShadowOpacity = double.NaN, ShadowBlur = double.NegativeInfinity, ShadowDepth = -20,
            DesktopWidth = 99999, DesktopLayout = " single ", Alignment = "invalid", LyricTransition = "fade",
            TransitionDurationMs = -1, ScrollDurationMs = double.PositiveInfinity, MainFocusScale = 7
        };
        invalid.Normalize();
        Check(invalid.FontFamily == original.FontFamily, "Font family rejects URI/path input");
        Check(invalid.MainFontSize == 26 && invalid.DesktopFontSize == 32, "Non-finite sizes fall back safely");
        Check(invalid.MainFontWeight == 100 && invalid.DesktopFontWeight == 900, "Font weights clamp");
        Check(invalid.SecondaryScale == .4 && invalid.BackgroundOpacity == 1, "Opacity/scale ranges clamp");
        Check(invalid.CurrentColor == "#BFD7FF" && invalid.InactiveColor == "#AAFF33" && invalid.TranslationColor == "#80AABBCC", "HEX colors normalize and validate");
        Check(invalid.ShadowOpacity == .9 && invalid.ShadowBlur == 8 && invalid.ShadowDepth == 0, "Invalid shadow values handled");
        Check(invalid.DesktopWidth == 2200 && invalid.DesktopLayout == "Single" && invalid.Alignment == "Center", "Layout choices normalize");
        Check(invalid.LyricTransition == "Fade" && invalid.TransitionDurationMs == 100 && invalid.ScrollDurationMs == 520 && invalid.MainFocusScale == 1.2, "Animation choices and finite timing normalize");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new DesktopLyricsWindow();
        var optionEvents = 0;
        overlay.OptionsChanged += () => optionEvents++;
        var appearance = new LyricAppearanceSettings { DesktopFontSize = 48, DesktopFontWeight = 800, DesktopWidth = 480,
            SecondaryScale = .6, Alignment = "Left", BackgroundColor = "#112233", BackgroundOpacity = .5,
            ShadowEnabled = false, ShowTrackInfo = false, DesktopTopmost = false };
        overlay.ApplyAppearance(appearance);
        Check(optionEvents == 0, "ApplyAppearance must not recursively save settings");
        Check(overlay.Width == 480 && !overlay.Topmost && overlay.LyricsFontSize == 48, "Desktop dimensions/topmost/size apply");
        var current = (TextBlock)overlay.FindName("CurrentLine");
        var secondary = (TextBlock)overlay.FindName("NextLine");
        Check(current.FontWeight.ToOpenTypeWeight() == 800 && current.TextAlignment == TextAlignment.Left && current.Effect is null, "Desktop text styling applies");
        Check(Math.Abs(secondary.FontSize - 28.8) < .01 && ((FrameworkElement)overlay.FindName("TrackHeader")).Visibility == Visibility.Collapsed, "Secondary scale and header switch apply");
        var surface = (Border)overlay.FindName("Surface");
        Check(surface.Background is SolidColorBrush brush && brush.Color == Color.FromRgb(0x11, 0x22, 0x33) && brush.Opacity == .5, "Background color and opacity apply");
        var document = LyricsService.ParseLrc("[00:01]The melody carries us home\n[00:01]旋律带我们回家\n[00:04]And the night is still young\n[00:04]夜还很漫长");
        overlay.SetTrack("A little light", "Embistic");
        overlay.UpdateLyrics(document, 1500);
        Check(current.Text == "The melody carries us home" && secondary.Text == "旋律带我们回家", "Translation layout selects translated lyric");
        for (var index = 0; index < 100; index++) overlay.UpdateLyrics(document, 1500 + index);
        Check(current.Text == "The melody carries us home" && secondary.Text == "旋律带我们回家", "Playback ticks retain active lyric");
        appearance.DesktopLayout = "NextLine";
        overlay.ApplyAppearance(appearance);
        Check(secondary.Text == "And the night is still young", "NextLine layout ignores translation");
        appearance.DesktopLayout = "Single";
        overlay.ApplyAppearance(appearance);
        Check(secondary.Visibility == Visibility.Collapsed, "Single layout hides secondary row");
        appearance.DesktopLayout = "Translation";
        appearance.DesktopFontSize = 56;
        appearance.DesktopWidth = 400;
        appearance.ShadowEnabled = true;
        overlay.ApplyAppearance(appearance);
        surface.Measure(new Size(400, double.PositiveInfinity));
        surface.Arrange(new Rect(0, 0, 400, surface.DesiredSize.Height));
        surface.UpdateLayout();
        Check(current.ActualHeight > 80 && surface.ActualHeight > current.ActualHeight, "Long lines wrap and expand overlay height");
        Check(current.Effect is System.Windows.Media.Effects.DropShadowEffect, "Shadow can be restored live");
        overlay.LyricsFontSize = 58;
        Check(optionEvents == 1 && appearance.DesktopFontSize == 56, "Legacy font control notifies without mutating caller settings");

        if (args.Length > 0)
        {
            overlay.ApplyAppearance(new LyricAppearanceSettings { DesktopWidth = 860, DesktopFontSize = 34, BackgroundOpacity = .88 });
            surface.Measure(new Size(860, double.PositiveInfinity));
            surface.Arrange(new Rect(0, 0, 860, surface.DesiredSize.Height));
            surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(860, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
            using var stream = File.Create(args[0]);
            encoder.Save(stream);
            Console.WriteLine("Rendered overlay preview: " + Path.GetFullPath(args[0]));
        }
        overlay.Close();
        app.Shutdown();
        Console.WriteLine($"PASS: {_checks} appearance validation and WPF overlay checks (no desktop window shown).");
    }
}
