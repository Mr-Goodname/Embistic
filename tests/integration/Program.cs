using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Xml.Linq;
using Embistic;
using Embistic.Controls;
using Embistic.Models;
using Embistic.Services;
using Track = Embistic.Models.Track;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var projectRoot = FindProjectRoot();
        var settingsBefore = SettingsFingerprint();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        // Load the production resource definitions without executing App.OnStartup
        // or acquiring the user's single-instance mutex.
        var appXaml = XElement.Load(Path.Combine(projectRoot, "src", "Embistic", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:Embistic;assembly=Embistic"),
            new XAttribute(XNamespace.Xmlns + "controls", "clr-namespace:Embistic.Controls;assembly=Embistic"),
            appXaml.Element(presentation + "Application.Resources")!.Elements());
        foreach (var element in resources.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:Embistic"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:Embistic;assembly=Embistic");
        foreach (var element in resources.Descendants().Where(element => element.Name.NamespaceName == "clr-namespace:Embistic.Controls"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:Embistic.Controls;assembly=Embistic");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        MainWindow? window = null;
        try
        {
            window = new MainWindow();
            // Exercise the real view and event wiring without starting playback, connecting,
            // checking for updates, opening desktop lyrics, or saving the user's preferences.
            window.Loaded -= (RoutedEventHandler)Method(window, "Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), window);
            window.SourceInitialized -= (EventHandler)Method(window, "Window_SourceInitialized").CreateDelegate(typeof(EventHandler), window);
            StopTimers(window);
            Set(window, "_saveSettings", new Action<AppSettings>(_ => { }));
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
            window.Width = 1480; window.Height = 1040;
            window.Opacity = 0;
            window.Show();
            Pump();
            Check(!Get<bool>(window, "_ready") && Get<object?>(window, "_player") is null && Get<object?>(window, "_emby") is null,
                "Fixture window never initializes audio or an Emby connection");
            Check(window.WindowStyle == WindowStyle.None && WindowChrome.GetWindowChrome(window) is { CaptionHeight: 40 }
                && Get<object?>(window, "_tray") is null, "Custom caption preserves WindowChrome while fixture never creates a tray icon");
            var captionPoint = window.PointToScreen(new Point(180, 20));
            Check(NativeHitTest(window, captionPoint) == 2 && NativeHitTest(window, window.PointToScreen(new Point(1, 200))) == 10,
                "Custom title and edge retain native caption drag/snap and resize hit testing");
            SendMessage(new WindowInteropHelper(window).Handle, 0xA3, new IntPtr(2), NativePoint(captionPoint)); Pump();
            Check(window.WindowState == WindowState.Maximized, "Native caption double-click maximizes the custom window");
            Click(window, "TitleMaximizeButton"); Pump();
            Click(window, "TitleFullscreenButton"); Pump();
            Check(window.WindowState == WindowState.Maximized && window.ResizeMode == ResizeMode.NoResize
                && WindowChrome.GetWindowChrome(window).CaptionHeight == 0, "Fullscreen caption button removes dragging and resize borders");
            Console.WriteLine($"Fullscreen window: {window.ActualWidth}×{window.ActualHeight}; primary screen: {SystemParameters.PrimaryScreenWidth}×{SystemParameters.PrimaryScreenHeight}");
            Click(window, "TitleFullscreenButton"); Pump();
            Check(window.WindowStyle == WindowStyle.None && window.ResizeMode == ResizeMode.CanResize && window.WindowState == WindowState.Normal,
                "Leaving fullscreen restores the resizable custom caption");

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
            Check(Find<Grid>(window, "WindowFrame").RowDefinitions[0].ActualHeight == 0
                && Grid.GetRowSpan(immersive) == 2, "Immersive canvas uses the title and bottom-player space");
            Pump(1950);
            Check(Find<Grid>(immersive, "TopOptions").Opacity == 0 && Find<Border>(window, "BottomPlayerBar").Opacity == 0
                && Find<Border>(window, "TitleBar").Opacity == 0, "Idle immersive options, title and player controls hide automatically");
            var options = Find<Popup>(immersive, "OptionsPopup");
            options.Child.Opacity = 0;
            Click(immersive, "OptionsButton"); Pump(1900);
            Check(options.IsOpen && Find<Grid>(immersive, "TopOptions").Opacity == 1
                && Find<Border>(window, "BottomPlayerBar").Opacity == 1, "Open lyrics popup keeps immersive controls visible");
            Click(immersive, "OptionsButton");
            var playButton = Find<Button>(window, "PlayPauseButton");
            Keyboard.Focus(playButton);
            playButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Tab)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump(1900);
            Check(playButton.IsKeyboardFocused && Find<Border>(window, "BottomPlayerBar").Opacity == 1,
                "Keyboard navigation keeps the focused playback controls visible");
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Pump(1900);
            Check(playButton.IsKeyboardFocused && Find<Border>(window, "BottomPlayerBar").Opacity == 0,
                "Mouse interaction lets controls hide even when a button retains keyboard focus");
            Keyboard.ClearFocus();

            var spectrumSettings = new SpectrumSettings { Enabled = true, ShowTop = true, ShowBottom = true, ShowLeft = true, ShowRight = true, ShowRing = true };
            immersive.ApplySpectrum(spectrumSettings); immersive.SetSpectrumPlaying(true); Pump();
            var spectra = new[] { immersive.Spectrum, Find<SpectrumView>(immersive, "SpectrumTop"), Find<SpectrumView>(immersive, "SpectrumLeft"),
                Find<SpectrumView>(immersive, "SpectrumRight"), Find<SpectrumView>(immersive, "SpectrumRing") };
            var bins = Enumerable.Repeat(.18f, 1025).ToArray();
            var beforeFrames = spectra.Select(view => view.RenderedFrameCount).ToArray();
            Task.Run(() => immersive.SetSpectrum(bins, 48000)).GetAwaiter().GetResult(); Pump(150);
            Check(spectra.Select((view, index) => view.IsVisible && view.RenderedFrameCount > beforeFrames[index]).All(value => value),
                "Capture-thread spectrum frames reach all four edges and the cover ring");
            var ring = spectra[4]; var artwork = Find<System.Windows.Shapes.Ellipse>(immersive, "AlbumArtwork");
            Check(ring.IsRadial && Math.Abs(ring.ActualWidth - ring.ActualHeight) < .01
                && Math.Abs(artwork.Width / ring.ActualWidth - .7) < .01, "Cover ring is square with the album artwork at seventy percent diameter");
            Check(spectra[2].ActualWidth > immersive.ActualHeight * .8 && spectra[3].ActualWidth > immersive.ActualHeight * .8,
                "Side spectra span the height of the immersive canvas");
            immersive.ApplySpectrum(new SpectrumSettings { ShowTop = false, ShowBottom = false, ShowLeft = false, ShowRight = false, ShowRing = true }); Pump();
            Check(ring.IsVisible && spectra.Take(4).All(view => !view.IsVisible), "Ring-only selection hides every edge spectrum");
            immersive.ApplySpectrum(settings.Spectrum); immersive.SetSpectrumPlaying(false);
            immersive.SetSpectrumStatus("Fixture spectrum status");
            Check(Find<Border>(immersive, "SpectrumStatusSurface").Visibility == Visibility.Visible, "New spectrum status is briefly displayed");
            Pump(3200);
            immersive.SetSpectrumStatus("Fixture spectrum status");
            Check(Find<Border>(immersive, "SpectrumStatusSurface").Visibility == Visibility.Collapsed, "Spectrum status disappears after three seconds without repeating unchanged text");
            immersive.SetSpectrumStatus("");

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
            CheckSeekEventWiring(window, immersive, track);

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
            settings.Display.RadioStations.Add(new RadioStation { Name = "测试电台", Url = "https://radio.invalid/live" });
            Invoke(window, "LibraryShortcut_Click", new Button { DataContext = window.LibraryShortcuts.Single(item => item.Id == "radio") }, new RoutedEventArgs());
            Pump();
            Check(window.LibraryItems.Count == 1 && window.LibraryItems[0].Track?.Path == "https://radio.invalid/live",
                "Saved radio stations remain accessible without connecting to Emby");

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
        immersive.ApplySpectrum(new SpectrumSettings { ShowRing = true, ShowBottom = true });
        immersive.SetSpectrumPlaying(true);
        Find<System.Windows.Media.RotateTransform>(immersive, "DiscRotation").Angle = 0;
        var bins = Enumerable.Range(0, 1024).Select(index => (float)(.35 * Math.Exp(-index / 600.0) * (.15 + .85 * Math.Pow(Math.Sin(index / 27.0), 2)))).ToArray();
        for (var frame = 0; frame < 58; frame++) { immersive.SetSpectrum(bins, 48000); Pump(40); }
        immersive.SetSpectrumStatus("");
        Render((FrameworkElement)window.Content, Path.Combine(directory, "lyrics.png"));
        immersive.SetSpectrumPlaying(false);
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
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
        {
            drawing.DrawRectangle((Brush)element.FindResource("Bg"), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(surface); SaveBitmap(bitmap, path);
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
    private static void CheckSeekEventWiring(MainWindow window, ImmersivePlayerView immersive, Track track)
    {
        var slider = Find<Slider>(window, "SeekBar");
        var originalMoveToPoint = slider.IsMoveToPointEnabled;
        var originalPosition = Get<long>(window, "_position");
        var originalDuration = Get<long>(window, "_duration");
        var originalVersion = Get<int>(window, "_playVersion");
        var originalRenderer = Get<object?>(window, "_renderer");
        var labels = 0;
        System.ComponentModel.PropertyChangedEventHandler labelChanged = (_, e) => { if (e.PropertyName == nameof(MainWindow.PositionLabel)) labels++; };
        window.PropertyChanged += labelChanged;
        // Standalone slider checks cover pointer coordinates and real dragging.
        // Here handled routed events exercise Root's event subscriptions without
        // capturing the user's mouse or invoking move-to-point input.
        slider.IsMoveToPointEnabled = false;
        try
        {
            Set(window, "_duration", 100000L); Set(window, "_position", 10000L);
            slider.Value = 20;
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Handled = true });
            Check(Get<bool>(window, "_seeking"), "Handled slider presses reach the main-window seek subscription");
            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Handled = true });
            Check(Get<bool>(window, "_seekReleasePending") && Get<long>(window, "_position") == 10000,
                "Slider release waits for remaining input handlers before committing a seek");
            slider.Value = 65;
            PumpUntil(() => !Get<bool>(window, "_seekReleasePending"));
            Check(Get<long>(window, "_position") == 65000 && !Get<bool>(window, "_seeking") && slider.Value == 65,
                "Deferred release reads the slider's final value instead of its earlier release value");
            Check(window.PositionLabel == "1:05" && labels > 0 && immersive.LyricsView.InterpolatedPositionMs == 65000
                && immersive.LyricsView.ActiveLineIndex == 5, "Committed seek immediately refreshes progress text and rebases the lyric clock");

            slider.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Handled = true });
            slider.Value = 80;
            slider.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.LostMouseCaptureEvent });
            PumpUntil(() => !Get<bool>(window, "_seeking"));
            Check(!Get<bool>(window, "_seekReleasePending") && Get<long>(window, "_position") == 65000
                && immersive.LyricsView.InterpolatedPositionMs == 65000 && slider.Value == 80,
                "External capture loss ends dragging without submitting the unfinished slider value");

            var gate = Get<SemaphoreSlim>(window, "_transportGate");
            foreach (var invalidation in new[] { "playback version", "current track", "output renderer" })
            {
                Set(window, "_playVersion", originalVersion); Set(window, "_renderer", null); SetCurrent(window, track);
                Set(window, "_duration", 100000L); Set(window, "_position", 31000L); slider.Value = 31;
                immersive.LyricsView.UpdatePlayback(31000, false, forceRebase: true);
                Check(gate.Wait(0), "Fixture acquires the transport gate before a delayed " + invalidation + " seek");
                Task pending;
                try
                {
                    pending = (Task)Invoke(window, "SeekAsync", 72000L)!;
                    Check(!pending.IsCompleted && Get<long>(window, "_position") == 31000,
                        "Seek waits for transport ownership before changing playback state: " + invalidation);
                    if (invalidation == "playback version") Set(window, "_playVersion", originalVersion + 1);
                    else if (invalidation == "current track") SetCurrent(window, new Track { Id = "seek-replacement", Title = "换曲测试", DurationMs = 100000 });
                    else Set(window, "_renderer", new DlnaDevice("seek-output", "测试输出", new Uri("http://fixture.invalid/control"), null));
                    Set(window, "_position", 12000L); slider.Value = 12;
                    immersive.LyricsView.UpdatePlayback(12000, false, forceRebase: true);
                }
                finally { gate.Release(); }
                PumpUntil(() => pending.IsCompleted); pending.GetAwaiter().GetResult();
                Check(Get<long>(window, "_position") == 12000 && slider.Value == 12 && immersive.LyricsView.InterpolatedPositionMs == 12000,
                    "Delayed seek preserves the replacement playback state after changing " + invalidation);
            }

            Set(window, "_playVersion", originalVersion); Set(window, "_renderer", null); SetCurrent(window, track);
            Check(gate.Wait(0), "Fixture acquires transport ownership for a valid delayed seek");
            Task valid;
            try { valid = (Task)Invoke(window, "SeekAsync", 43000L)!; }
            finally { gate.Release(); }
            PumpUntil(() => valid.IsCompleted); valid.GetAwaiter().GetResult();
            Check(Get<long>(window, "_position") == 43000 && slider.Value == 43 && immersive.LyricsView.InterpolatedPositionMs == 43000,
                "A delayed seek still commits when the track, version and output remain current");
        }
        finally
        {
            window.PropertyChanged -= labelChanged;
            slider.IsMoveToPointEnabled = originalMoveToPoint;
            Set(window, "_seeking", false); Set(window, "_seekReleasePending", false);
            Set(window, "_playVersion", originalVersion); Set(window, "_renderer", originalRenderer);
            Set(window, "_duration", originalDuration); Set(window, "_position", originalPosition); SetCurrent(window, track);
            slider.Value = originalDuration > 0 ? originalPosition * 100d / originalDuration : 0;
            immersive.LyricsView.UpdatePlayback(originalPosition, false, forceRebase: true);
        }
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) Pump(20);
        if (!condition()) throw new TimeoutException("Fixture UI operation did not complete within three seconds.");
    }
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    private static IntPtr NativePoint(Point point) => new(unchecked(((int)(short)Math.Round(point.Y) << 16) | (ushort)(short)Math.Round(point.X)));
    private static int NativeHitTest(Window window, Point point) => (int)SendMessage(new WindowInteropHelper(window).Handle, 0x84, IntPtr.Zero, NativePoint(point));
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
}
