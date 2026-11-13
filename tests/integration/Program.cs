using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Embistic;
using Embistic.Controls;
using Embistic.Models;
using Embistic.Services;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var projectRoot = FindProjectRoot();
        var settingsBefore = SettingsFingerprint();
        var app = new FixtureApp { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        app.StartupUri = null;
        MainWindow? window = null;
        try
        {
            window = new MainWindow();
            // Exercise the real view and event wiring without starting playback, connecting,
            // checking for updates, opening desktop lyrics, or saving the user's preferences.
            window.Loaded -= (RoutedEventHandler)Method(window, "Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), window);
            StopTimers(window);
            Set(window, "_history", null);
            (Get<object?>(window, "_downloads") as IDisposable)?.Dispose();
            Set(window, "_downloads", null);
            var settings = new AppSettings
            {
                CheckUpdatesAutomatically = false, Volume = 36,
                Appearance = new LyricAppearanceSettings { AnimationsEnabled = false },
                Theme = new ThemeSettings { Mode = "Dark", FollowSystemAccent = false },
                Display = new DisplaySettings(),
                Spectrum = new SpectrumSettings { Enabled = true }
            };
            Set(window, "_settings", settings);
            Get<ThemeService>(window, "_theme").Apply(settings.Theme);
            Invoke(window, "ApplyAppearance", settings.Appearance);
            Invoke(window, "ApplySpectrumSettings", settings.Spectrum);
            Invoke(window, "ApplyDisplaySettings");
            Find<TextBox>(window, "ServerBox").Clear();
            Find<TextBox>(window, "UsernameBox").Clear();
            Find<PasswordBox>(window, "ApiKeyBox").Clear();
            window.Users.Clear();
            Find<Slider>(window, "VolumeSlider").Value = settings.Volume;
            Invoke(window, "Notify", nameof(MainWindow.VolumeLabel));
            // Popups own separate HWNDs; keep the fixture popup invisible even if Windows
            // brings its placement back inside the monitor's working area.
            Find<Popup>(window, "OutputPopup").Child.Opacity = 0;
            window.AudioDevices.Add(new AudioDevice("", "系统默认输出"));
            // A fixture device prevents Output_Click from triggering automatic SSDP discovery.
            window.DlnaDevices.Add(new DlnaDevice("fixture-speaker", "演示音箱", new Uri("http://fixture.invalid/control"), null));
            window.DlnaStatus = "演示设备 · 未连接音箱";
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = -10000;
            window.Width = 1480; window.Height = 960;
            window.Opacity = 0;
            window.Show();
            Pump();
            Check(!Get<bool>(window, "_ready") && Get<object?>(window, "_player") is null && Get<object?>(window, "_emby") is null,
                "Fixture window never initializes audio or an Emby connection");

            var coversDirectory = Path.Combine(projectRoot, "artifacts", "integration", "covers");
            var albums = CreateAlbums(coversDirectory);
            var track = new Track
            {
                Id = "demo-night-flight", Title = "夜航", Artist = "Embistic 演示", Album = "夜航", AlbumId = albums[0].Id,
                DurationMs = 238000, Container = "flac", BitDepth = 24, SampleRate = 48000,
                ImageUrl = albums[0].ImageUrl, IsFavorite = true
            };
            SetCurrent(window, track);
            var immersive = Find<ImmersivePlayerView>(window, "ImmersivePlayer");
            Check(Find<TextBlock>(immersive, "TrackTitle").Text == track.Title
                && Find<TextBlock>(immersive, "TrackArtist").Text == track.Artist,
                "Current track propagates to the real immersive view");
            Set(window, "_playing", true);
            Invoke(window, "NotifyNowPlaying");
            Click(window, "PlayerCoverButton");
            Pump();
            Check(Get<string>(window, "_section") == "lyrics" && immersive.IsVisible,
                "Clicking the bottom cover opens immersive lyrics");
            Check(Get<bool>(immersive, "_playing"), "Root playback state reaches the immersive view");

            var lines = new[]
            {
                "夜色落在窗边", "城市渐渐安静", "沿着熟悉的街道", "让旋律陪我回家", "风从远处经过", "灯光映着晚霞", "把今天写成一首歌", "明天再轻轻唱起"
            };
            var document = new LyricsDocument
            {
                Source = "原创演示歌词", IsSynced = true,
                Lines = lines.Select((text, index) => new LyricsLine(TimeSpan.FromSeconds(index * 12), text)).ToArray()
            };
            Set(window, "_document", document);
            Set(window, "_position", 41000L); Set(window, "_duration", track.DurationMs);
            Invoke(window, "UpdateLyricsBindings");
            Invoke(window, "Timer_Tick", null, EventArgs.Empty);
            Pump();
            Check(immersive.LyricsView.ActiveLineIndex == 3 && window.CurrentLyric == lines[3],
                "Root timer selects the same current lyric in the sidebar and immersive view");
            Check(Math.Abs(Find<Slider>(window, "SeekBar").Value - 41000 * 100.0 / track.DurationMs) < .01,
                "Root progress updates the bottom seek control");
            Set(window, "_playing", false); Invoke(window, "NotifyNowPlaying");
            Check(!Get<bool>(immersive, "_playing") && !immersive.Spectrum.IsPlaying,
                "Pausing stops immersive playback and spectrum drawing");

            var sectionBeforePopup = Get<string>(window, "_section");
            Click(window, "OutputButton");
            Pump();
            Check(Find<Popup>(window, "OutputPopup").IsOpen && Get<string>(window, "_section") == sectionBeforePopup,
                "Output button opens its popup without navigating away");
            Click(window, "OutputButton");
            Check(!Find<Popup>(window, "OutputPopup").IsOpen, "Output popup toggles closed");

            var collection = Enumerable.Range(0, 405).Select(index => new Track { Id = "fixture-" + index, Title = "测试歌曲 " + index }).ToArray();
            Invoke(window, "ShowCollection", collection, "分页测试", "本地测试集合");
            Check(window.Tracks.Count == 200 && window.Tracks[0].Id == "fixture-0", "Collection shows its first 200 tracks without an Emby connection");
            Click(window, "NextPageButton"); Pump();
            Check(window.Tracks.Count == 200 && window.Tracks[0].Id == "fixture-200", "Next-page button stays inside the collection");
            Click(window, "PlayerCoverButton"); Pump();
            Click(immersive, "BackButton"); Pump();
            Check(Get<string>(window, "_section") == "collection" && window.Tracks[0].Id == "fixture-200"
                && window.PageTitle == "分页测试", "Returning from lyrics preserves the collection, title and page");
            Click(window, "NextPageButton"); Pump();
            Check(window.Tracks.Count == 5 && window.Tracks[0].Id == "fixture-400"
                && !Find<Button>(window, "NextPageButton").IsEnabled, "Last collection page has only the remaining tracks");
            Click(window, "PreviousPageButton"); Pump();
            Check(window.Tracks[0].Id == "fixture-200", "Previous-page button returns to the prior collection page");

            Invoke(window, "ShowPage", "home"); Pump();
            var sidebar = Find<Border>(window, "SidebarPlayer");
            var layout = (Grid)sidebar.Parent;
            var sidebarColumn = layout.ColumnDefinitions[Grid.GetColumn(sidebar)];
            var expandedWidth = sidebarColumn.ActualWidth;
            settings.Display.ShowSidebarPlayer = false; Invoke(window, "ApplyDisplaySettings"); Pump();
            Check(sidebar.Visibility == Visibility.Collapsed && sidebarColumn.ActualWidth == 0,
                "Hiding the sidebar releases its layout column");
            settings.Display.ShowSidebarPlayer = true; Invoke(window, "ApplyDisplaySettings"); Pump();
            Check(sidebar.Visibility == Visibility.Visible && Math.Abs(sidebarColumn.ActualWidth - expandedWidth) < 1,
                "Showing the sidebar restores its width");
            settings.Display.DiscAnimation = false; Invoke(window, "ApplyDisplaySettings");
            Set(window, "_playing", true); Invoke(window, "NotifyNowPlaying");
            Click(window, "PlayerCoverButton"); Pump();
            Check(!immersive.DiscAnimationEnabled && !Get<bool>(immersive, "_rotating"),
                "Disabling disc animation prevents rotation while playing");
            settings.Display.DiscAnimation = true; Invoke(window, "ApplyDisplaySettings"); Pump();
            Check(immersive.DiscAnimationEnabled && (!SystemParameters.ClientAreaAnimation || Get<bool>(immersive, "_rotating")),
                "Disc animation setting reaches the playing, visible record");
            Set(window, "_playing", false); Invoke(window, "NotifyNowPlaying");
            Check(!Get<bool>(immersive, "_rotating"), "Pausing stops record rotation");
            Check(!Get<AudioSpectrumService>(window, "_spectrumService").IsRunning, "UI checks never capture a real audio endpoint");

            if (args.Contains("--screenshots"))
                CaptureDocumentation(window, settings, albums, track, projectRoot);
            Check(SettingsFingerprint() == settingsBefore, "UI checks leave the user's saved settings unchanged");
            Console.WriteLine($"Completed: {_checks} main-window integration checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is TargetInvocationException { InnerException: { } inner } ? inner : exception);
            return 1;
        }
        finally
        {
            if (window is not null)
            {
                StopTimers(window);
                Set(window, "_closing", true); Set(window, "_allowClose", true);
                foreach (var field in new[] { "_spectrumService", "_theme", "_downloads", "_dlna", "_lyrics" })
                    (Get<object?>(window, field) as IDisposable)?.Dispose();
                window.Close();
            }
            app.Shutdown();
        }
    }

    private static void CaptureDocumentation(MainWindow window, AppSettings settings, IReadOnlyList<Album> albums, Track track, string root)
    {
        var directory = Path.Combine(root, "docs", "images");
        Directory.CreateDirectory(directory);
        foreach (var album in albums) window.RecentAlbums.Add(album);
        foreach (var album in albums.Take(3)) window.FavoriteAlbums.Add(album);
        window.RecentPlayedTracks.Add(track);
        window.FrequentTracks.Add(track);
        window.RecentPlayedAlbums.Add(albums[0]);
        window.FrequentAlbums.Add(albums[0]);
        settings.Display.HomeSections = ["recentAlbums", "recentTracks", "favorites", "playedAlbums", "frequentTracks", "frequentAlbums"];
        settings.Display.DiscAnimation = false;
        Invoke(window, "ApplyDisplaySettings");
        window.ConnectionLabel = "●  演示音乐库";
        window.Status = "界面演示 · 专辑、曲目与歌词均为本地生成的示例";
        Set(window, "_playing", true); Invoke(window, "NotifyNowPlaying");
        Invoke(window, "ShowPage", "home"); Pump(200);
        Render((FrameworkElement)window.Content, Path.Combine(directory, "home.png"));

        Click(window, "PlayerCoverButton"); Pump(100);
        var immersive = Find<ImmersivePlayerView>(window, "ImmersivePlayer");
        Invoke(window, "Timer_Tick", null, EventArgs.Empty);
        var spectrum = immersive.Spectrum;
        spectrum.IsPlaying = true;
        var bins = Enumerable.Range(0, 1024).Select(index => (float)(.35 * Math.Exp(-index / 600.0) * (.15 + .85 * Math.Pow(Math.Sin(index / 27.0), 2)))).ToArray();
        for (var frame = 0; frame < 10; frame++) { spectrum.SetSpectrum(bins, 48000); Pump(35); }
        immersive.SetSpectrumStatus("界面演示 · 原创示例歌词 · 频谱使用合成测试信号");
        Pump(80);
        Render((FrameworkElement)window.Content, Path.Combine(directory, "lyrics.png"));
        spectrum.IsPlaying = false;
        Console.WriteLine("Documentation screenshots: " + directory);
    }

    private static IReadOnlyList<Album> CreateAlbums(string directory)
    {
        Directory.CreateDirectory(directory);
        var names = new[] { "夜航", "海岸线", "晴空", "暮色", "风与树", "远山" };
        var colors = new[] { ("#122438", "#567FA4"), ("#153D43", "#66ACA2"), ("#305C78", "#AAD4D4"), ("#55374A", "#D18E82"), ("#243F38", "#93AB7F"), ("#31374B", "#9294BE") };
        var result = new List<Album>();
        for (var index = 0; index < names.Length; index++)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                var first = (Color)ColorConverter.ConvertFromString(colors[index].Item1);
                var second = (Color)ColorConverter.ConvertFromString(colors[index].Item2);
                drawing.DrawRectangle(new LinearGradientBrush(first, second, 70), null, new Rect(0, 0, 640, 640));
                drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(180, 226, 221, 192)), null, new Point(440 - index * 17, 195), 75, 75);
                for (var layer = 0; layer < 4; layer++)
                {
                    var geometry = new StreamGeometry();
                    using (var path = geometry.Open())
                    {
                        path.BeginFigure(new Point(0, 310 + layer * 50), true, true);
                        path.BezierTo(new Point(170, 220 + layer * 44), new Point(360, 430 + layer * 35), new Point(640, 285 + layer * 55), true, false);
                        path.LineTo(new Point(640, 640), true, false); path.LineTo(new Point(0, 640), true, false);
                    }
                    drawing.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(100 + layer * 28), first.R, first.G, first.B)), null, geometry);
                }
                DrawText(drawing, names[index], 64, new Point(48, 440), Brushes.White);
                DrawText(drawing, "EMBISTIC / DEMO " + (index + 1).ToString("00"), 17, new Point(52, 552), new SolidColorBrush(Color.FromRgb(187, 205, 218)));
            }
            var pathName = Path.Combine(directory, "cover-" + index + ".png");
            var bitmap = new RenderTargetBitmap(640, 640, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); SaveBitmap(bitmap, pathName);
            result.Add(new Album { Id = "demo-album-" + index, Title = names[index], Artist = "Embistic 演示", Year = 2026, ImageUrl = new Uri(pathName).AbsoluteUri });
        }
        return result;
    }

    private static void DrawText(DrawingContext drawing, string value, double size, Point point, Brush brush) =>
        drawing.DrawText(new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, 1), point);
    private static void Render(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); SaveBitmap(bitmap, path);
    }
    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "Embistic", "Embistic.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Run the integration project from inside the Embistic repository.");
    }
    private static string SettingsFingerprint() => File.Exists(SettingsService.SettingsPath)
        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SettingsService.SettingsPath))) : "missing";
    private static void StopTimers(MainWindow window)
    {
        foreach (var name in new[] { "_timer", "_searchTimer", "_volumeTimer" }) Get<DispatcherTimer>(window, name).Stop();
    }
    private static T Find<T>(FrameworkElement element, string name) where T : class =>
        element.FindName(name) as T ?? throw new InvalidOperationException("Missing UI element: " + name);
    private static T Get<T>(object target, string name) => (T)(target.GetType().GetField(name, Private)?.GetValue(target))!;
    private static void Set(object target, string name, object? value) =>
        (target.GetType().GetField(name, Private) ?? throw new MissingFieldException(name)).SetValue(target, value);
    private static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name, Private) ?? throw new MissingMethodException(name);
    private static object? Invoke(object target, string name, params object?[] values) => Method(target, name).Invoke(target, values);
    private static void SetCurrent(MainWindow window, Track track) => typeof(MainWindow).GetProperty(nameof(MainWindow.Current))!.SetValue(window, track);
    private static void Click(FrameworkElement element, string name) => Find<Button>(element, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + description);
        _checks++; Console.WriteLine("PASS: " + description);
    }
    private static void Pump(int milliseconds = 80)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private sealed class FixtureApp : App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }
}
