using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;
using Embistic.Controls;

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
        Check(invalid.LyricTransition == "Fade" && invalid.TransitionDurationMs == 100 && invalid.ScrollDurationMs == 520 && invalid.MainFocusScale == 1.5, "Animation choices and finite timing normalize");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new DesktopLyricsWindow();
        var optionEvents = 0;
        overlay.OptionsChanged += () => optionEvents++;
        var appearance = new LyricAppearanceSettings { DesktopFontSize = 48, DesktopFontWeight = 800, DesktopWidth = 480,
            SecondaryScale = .6, Alignment = "Left", BackgroundColor = "#112233", BackgroundOpacity = .5,
            ShadowEnabled = false, ShowTrackInfo = false, DesktopTopmost = false, DesktopMainAlignment = "Left", DesktopSecondaryAlignment = "Right" };
        overlay.ApplyAppearance(appearance);
        Check(optionEvents == 0, "ApplyAppearance must not recursively save settings");
        Check(overlay.Width == 480 && !overlay.Topmost && overlay.LyricsFontSize == 48, "Desktop dimensions/topmost/size apply");
        var current = (OutlinedLyricsText)overlay.FindName("CurrentLine");
        var secondary = (OutlinedLyricsText)overlay.FindName("NextLine");
        Check(current.FontWeight.ToOpenTypeWeight() == 800 && current.TextAlignment == TextAlignment.Left && current.Effect is null, "Desktop text styling applies");
        Check(Math.Abs(secondary.FontSize - 28.8) < .01 && ((FrameworkElement)overlay.FindName("TrackHeader")).Visibility == Visibility.Collapsed, "Secondary scale and header switch apply");
        var surface = (Border)overlay.FindName("Surface");
        Check(surface.Background is SolidColorBrush transparent && transparent.Color.A == 0 && surface.BorderThickness == new Thickness(0), "Legacy background opacity still defaults to a fully transparent borderless overlay");
        Check(secondary.TextAlignment == TextAlignment.Right, "Desktop lines have independent alignment");
        appearance.DesktopBackgroundEnabled = true;
        appearance.SungColor = "#12ABCD"; appearance.UnsungColor = "#FEDCBA";
        appearance.SungOutlineColor = "#FF0000"; appearance.UnsungOutlineColor = "#00FF00";
        appearance.SungOutlineWidth = 3.2; appearance.UnsungOutlineWidth = .8;
        overlay.ApplyAppearance(appearance);
        Check(surface.Background is SolidColorBrush brush && brush.Color.A == 0, "Legacy background preference leaves the non-hovered overlay fully transparent");
        Check(current.SungOutlineWidth == 3.2 && current.UnsungOutlineWidth == .8 && ((SolidColorBrush)current.SungBrush).Color == Color.FromRgb(0x12, 0xAB, 0xCD), "Sung and unsung text use independent paints and outline widths");
        var document = LyricsService.ParseLrc("[00:01]The melody carries us home\n[00:01]旋律带我们回家\n[00:04]And the night is still young\n[00:04]夜还很漫长");
        overlay.SetTrack("A little light", "Embistic");
        overlay.UpdateLyrics(document, 1500);
        Check(current.Text == "The melody carries us home" && secondary.Text == "旋律带我们回家", "Translation layout selects translated lyric");
        for (var index = 0; index < 100; index++) overlay.UpdateLyrics(document, 1500 + index);
        Check(current.Text == "The melody carries us home" && secondary.Text == "旋律带我们回家", "Playback ticks retain active lyric");
        var timed = LyricsService.ParseLrc("[00:01]<00:01>你<00:01.4>好<00:03>");
        overlay.UpdateLyrics(timed, 1700);
        Check(current.Words.Count == 2 && current.PlaybackPositionMs == 1700 && current.Words[0].End.TotalMilliseconds == 1400, "Desktop karaoke uses parsed word timing on playback updates");
        var glyph = new OutlinedLyricsText { FontSize = 42, Foreground = new SolidColorBrush(Color.FromRgb(240, 243, 248)) };
        glyph.ApplyLyricsStyle(new LyricAppearanceSettings { SungColor = "#12ABCD", SungOutlineWidth = 1.4, UnsungOutlineWidth = 1.4 });
        glyph.SetLine(timed.Lines[0], "你好");
        glyph.Measure(new Size(250, 100)); glyph.Arrange(new Rect(0, 0, 250, 100));
        glyph.UpdatePlayback(900);
        var beforePixels = RenderPixels(glyph, 250, 100);
        glyph.UpdatePlayback(1700);
        var duringPixels = RenderPixels(glyph, 250, 100);
        Check(!beforePixels.SequenceEqual(duringPixels) && duringPixels.Where((_, i) => i % 4 == 3).Any(alpha => alpha > 200), "Word progress changes rendered glyph colors while preserving visible text ink");
        glyph.KaraokeEnabled = false;
        Check(RenderPixels(glyph, 250, 100).SequenceEqual(beforePixels), "Inactive lyric rows suppress the sung paint even after word progress advances");
        overlay.UpdateLyrics(document, 1500);
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

    private static byte[] RenderPixels(FrameworkElement element, int width, int height)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
