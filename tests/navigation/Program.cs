using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Embistic;
using Embistic.Controls;
using Embistic.Models;
using Embistic.Services;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks;
    private static string _root = "";
    [STAThread]
    private static int Main()
    {
        _root = FindRoot(); var fingerprint = Fingerprint();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        LoadResources(app);
        using var server = new LibraryServer(); MainWindow? window = null;
        try
        {
            window = CreateWindow(server.Url);
            TestHome(window, server);
            TestTrackContext(window, server);
            TestLibrary(window, server);
            Check(Field<object?>(window, "_player") is null && !Field<AudioSpectrumService>(window, "_spectrumService").IsRunning,
                "Navigation never initializes audio playback or endpoint capture");
            Check(Fingerprint() == fingerprint, "Navigation checks leave the user's saved preferences unchanged");
            Console.WriteLine($"PASS: {_checks} real navigation, back-history and page-restoration checks."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex); return 1; }
        finally
        {
            if (window is not null)
            {
                Set(window, "_closing", true); Set(window, "_allowClose", true);
                foreach (var name in new[] { "_timer", "_searchTimer", "_volumeTimer", "_inlineSaveTimer" }) Field<DispatcherTimer>(window, name).Stop();
                foreach (var name in new[] { "_emby", "_spectrumService", "_theme", "_downloads", "_dlna", "_lyrics" }) (Field<object?>(window, name) as IDisposable)?.Dispose();
                window.Close();
            }
            app.Shutdown();
        }
    }

    private static MainWindow CreateWindow(string url)
    {
        var window = new MainWindow();
        window.Loaded -= (RoutedEventHandler)Method(window, "Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), window);
        window.SourceInitialized -= (EventHandler)Method(window, "Window_SourceInitialized").CreateDelegate(typeof(EventHandler), window);
        foreach (var name in new[] { "_timer", "_searchTimer", "_volumeTimer" }) Field<DispatcherTimer>(window, name).Stop();
        Set(window, "_saveSettings", new Action<AppSettings>(_ => { })); Set(window, "_history", null);
        (Field<object?>(window, "_downloads") as IDisposable)?.Dispose(); Set(window, "_downloads", null);
        var settings = new AppSettings { ServerUrl = url, UserId = "navigation-fixture", ApiKey = "fixture", CheckUpdatesAutomatically = false,
            CloseToTray = false, Appearance = new LyricAppearanceSettings { AnimationsEnabled = false } };
        Set(window, "_settings", settings); Set(window, "_emby", new EmbyClient(settings));
        Invoke(window, "ApplyAppearance", settings.Appearance); Invoke(window, "ApplyDisplaySettings");
        foreach (var album in Enumerable.Range(0, 18).Select(i => new Album { Id = "album-" + i, Title = "测试专辑 " + i, Artist = "演示艺术家" })) window.RecentAlbums.Add(album);
        Invoke(window, "ApplyHomeModules");
        window.ShowInTaskbar = false; window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000; window.Width = 1420; window.Height = 920;
        window.Show(); Invoke(window, "ShowPage", "home"); Set(window, "_ready", true); Pump(120);
        return window;
    }

    private static void TestHome(MainWindow window, LibraryServer server)
    {
        var home = Named<ScrollViewer>(window, "HomePage"); home.ScrollToVerticalOffset(300); Pump(60);
        var offset = home.VerticalOffset;
        Check(offset > 100 && Named<RadioButton>(window, "HomeNav").IsChecked == true, "Home fixture is scrolled and its sidebar tab is selected");
        ClickAlbumCard(window, "album-0"); Wait(() => Section(window) == "album" && window.Tracks.Count == 200 && window.Queue.Count == 405);
        Check(window.PageTitle == "测试专辑 0" && Named<Button>(window, "NavigationBackButton").IsEnabled,
            "Clicking a real recommended album exposes the main return button");
        Check(Named<RadioButton>(window, "HomeNav").IsChecked == true, "An album retains the originating home sidebar context");
        Capture(window, "album-back-button.png");
        var queue = window.Queue.Select(x => x.Id).ToArray(); var requests = server.RequestCount;
        Click(window, "NavigationBackButton"); Pump(120);
        Check(Section(window) == "home" && window.PageTitle == "为你推荐", "The visible return button restores the originating home page");
        Check(Math.Abs(home.VerticalOffset - offset) < 1, "Returning from an album preserves the exact home scroll position");
        Check(server.RequestCount == requests && queue.SequenceEqual(window.Queue.Select(x => x.Id)), "Returning restores cached content without HTTP reload or rebuilding the playback queue");
        Check(!Named<Button>(window, "NavigationBackButton").IsEnabled, "Back history ends at the sidebar root without creating a loop");
        Capture(window, "home-returned.png");
        ClickAlbumCard(window, "album-1"); Wait(() => Section(window) == "album" && window.Tracks.FirstOrDefault()?.AlbumId == "album-1");
        var homeTab = Named<RadioButton>(window, "HomeNav"); Check(homeTab.IsChecked == true, "Home remains selected before clicking its already-selected tab");
        homeTab.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(() => Section(window) == "home");
        Check(Section(window) == "home" && !window.CanNavigateBack, "Clicking an already-selected home tab returns directly to its root");
        Wait(() => window.RecentAlbums.Count == 12); Pump(120);
    }

    private static void TestTrackContext(MainWindow window, LibraryServer server)
    {
        ClickNav(window, "tracks"); Wait(() => Section(window) == "tracks" && window.Tracks.Count == 200);
        Named<TextBox>(window, "SearchBox").Text = "quiet";
        Wait(() => Field<string>(window, "_search") == "quiet" && window.Tracks.FirstOrDefault()?.Id.StartsWith("quiet-") == true);
        Click(window, "NextPageButton"); Wait(() => Field<int>(window, "_page") == 1 && window.Tracks.First().Id == "quiet-200");
        var list = Named<ListBox>(window, "TrackList"); list.SelectedIndex = 8;
        var scroll = Children<ScrollViewer>(list).First(); scroll.ScrollToVerticalOffset(12); Pump(60);
        var offset = scroll.VerticalOffset; var selection = list.SelectedItem;
        OpenCurrentAlbumMenu(window, window.Tracks[8]); Wait(() => Section(window) == "album" && window.Tracks.FirstOrDefault()?.AlbumId == "album-target");
        var requests = server.RequestCount;
        NavigationCommands.BrowseBack.Execute(null, window); Pump(120);
        Check(Section(window) == "tracks" && Field<int>(window, "_page") == 1 && window.Tracks.First().Id == "quiet-200",
            "Back restores the exact 200-item page and result slice from a track context menu");
        Check(Field<string>(window, "_search") == "quiet" && Named<TextBox>(window, "SearchBox").Text == "quiet" && window.PageTitle == "搜索 · quiet",
            "Back restores search filters and the heading captured before album navigation");
        Check(ReferenceEquals(list.SelectedItem, selection) && Math.Abs(scroll.VerticalOffset - offset) < 1,
            "Back restores track selection and virtualized list scroll position");
        Check(server.RequestCount == requests, "Restoring a search page does not issue a duplicate search request");
        var binding = window.InputBindings.OfType<KeyBinding>().Any(b => b.Command == NavigationCommands.BrowseBack && b.Key == Key.Left && b.Modifiers == ModifierKeys.Alt);
        Check(binding, "Alt+Left invokes the same routed back command as the return button");
        var historyBefore = HistoryCount(window);
        Click(window, "PlayerCoverButton"); Pump(60);
        Check(Section(window) == "lyrics" && HistoryCount(window) == historyBefore, "Immersive playback does not create an ordinary navigation-history entry");
        Click(Named<ImmersivePlayerView>(window, "ImmersivePlayer"), "BackButton"); Pump(100);
        Check(Section(window) == "tracks" && Field<int>(window, "_page") == 1 && Field<string>(window, "_search") == "quiet" && HistoryCount(window) == historyBefore,
            "Leaving immersive playback restores the original paged search without consuming its history");
        var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.XButton1) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        window.RaiseEvent(mouse); Pump(100);
        Check(mouse.Handled && Field<string>(window, "_search") == "" && Named<TextBox>(window, "SearchBox").Text == "" && Field<int>(window, "_page") == 0,
            "Mouse back restores the prior unfiltered root rather than retaining the newly typed query");
    }

    private static void TestLibrary(MainWindow window, LibraryServer server)
    {
        ClickNav(window, "library"); Pump(60);
        var shortcut = Children<Button>(Named<ScrollViewer>(window, "LibraryOverviewPage")).First(b => b.DataContext is LibraryShortcut { Id: "folders" });
        shortcut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(() => window.LibraryItems.Any(x => x.Id == "music-root"));
        DoubleClickLibrary(window, "music-root"); Wait(() => window.LibraryItems.Any(x => x.Id == "folder-0"));
        var list = Named<ListBox>(window, "LibraryList"); var scroll = Children<ScrollViewer>(list).First();
        scroll.ScrollToVerticalOffset(8); Pump(60); var offset = scroll.VerticalOffset;
        DoubleClickLibrary(window, "folder-12"); Wait(() => window.LibraryItems.Any(x => x.Id == "album-child"));
        DoubleClickLibrary(window, "album-child"); Wait(() => Section(window) == "album" && window.Tracks.FirstOrDefault()?.AlbumId == "album-child");
        Click(window, "NavigationBackButton"); Pump(100);
        Check(Section(window) == "browse" && window.LibraryItems.Any(x => x.Id == "album-child") && window.PageTitle == "文件夹 12",
            "Album back returns to its precise media-library folder");
        Click(window, "LibraryBackButton"); Pump(100);
        Check(window.PageTitle == "音乐根目录" && window.LibraryItems.Count == 30 && Math.Abs(scroll.VerticalOffset - offset) < 1,
            "The existing library up button restores its parent list and scroll from the same history");
        Click(window, "NavigationBackButton"); Pump(60);
        Check(window.LibraryItems.Count == 1 && window.LibraryItems[0].Id == "music-root", "Nested folder back returns to the server music roots");
        Click(window, "NavigationBackButton"); Pump(60);
        Check(Section(window) == "library" && Named<RadioButton>(window, "LibraryNav").IsChecked == true && !window.CanNavigateBack,
            "Folder history returns to the correctly selected media-library sidebar root");
        var artists = Children<Button>(Named<ScrollViewer>(window, "LibraryOverviewPage")).First(b => b.DataContext is LibraryShortcut { Id: "artists" });
        artists.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Wait(() => window.LibraryItems.Any(x => x.Id == "artist-1"));
        DoubleClickLibrary(window, "artist-1"); Wait(() => Section(window) == "collection" && window.Tracks.Count == 200);
        Click(window, "NextPageButton"); Wait(() => Field<int>(window, "_page") == 1);
        OpenCurrentAlbumMenu(window, window.Tracks[1]); Wait(() => Section(window) == "album");
        Click(window, "NavigationBackButton"); Pump(100);
        Check(Section(window) == "collection" && Field<int>(window, "_page") == 1 && window.Tracks.First().Id == "artist-200",
            "Returning to a collection preserves its complete source and current 200-item page");
        Click(window, "NextPageButton"); Pump(80);
        Check(window.Tracks.Count == 5 && window.Tracks.First().Id == "artist-400", "The restored collection remains pageable without switching to all tracks");
        Click(window, "NavigationBackButton"); Pump(80);
        Check(Section(window) == "browse" && window.LibraryItems.First().Id == "artist-1", "Collection back restores the originating artist list");
        ClickNav(window, "home"); Wait(() => Section(window) == "home");
        Check(!window.CanNavigateBack && Named<RadioButton>(window, "HomeNav").IsChecked == true,
            "Explicit sidebar switches establish a clean root instead of mixing unrelated histories");
    }

    private static void OpenCurrentAlbumMenu(MainWindow window, Track track)
    {
        Set(window, "_current", track); Invoke(window, "NotifyNowPlaying");
        Invoke(window, "PlayerCover_ContextMenuOpening", Named<Button>(window, "PlayerCoverButton"), null);
        Named<ContextMenu>(window, "PlayerCoverMenu").Items.OfType<MenuItem>().Single(item => item.Header as string == "转到专辑").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
    private static void ClickAlbumCard(MainWindow window, string id) => Children<Button>(Named<ScrollViewer>(window, "HomePage")).First(b => b.DataContext is Album a && a.Id == id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void ClickNav(MainWindow window, string section)
    {
        var button = Children<RadioButton>(window).Single(b => b.Tag as string == section && b.GroupName == "Nav");
        button.IsChecked = true; button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void DoubleClickLibrary(MainWindow window, string id)
    {
        var list = Named<ListBox>(window, "LibraryList"); var item = window.LibraryItems.Single(x => x.Id == id);
        list.ScrollIntoView(item); Pump(40); var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = container };
        list.RaiseEvent(click);
    }
    private static int HistoryCount(MainWindow window) => ((System.Collections.ICollection)Field<object>(window, "_navigationHistory")).Count;
    private static string Section(MainWindow window) => Field<string>(window, "_section");
    private static void Click(FrameworkElement root, string name) => Named<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);
    private static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name, Private)!;
    private static object? Invoke(object target, string name, params object?[] args) => Method(target, name).Invoke(target, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    { if (root is T value) yield return value; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Children<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; Console.WriteLine("PASS: " + message); }
    private static void Wait(Func<bool> condition)
    { var started = Stopwatch.StartNew(); while (!condition() && started.ElapsedMilliseconds < 5000) Pump(20); if (!condition()) throw new TimeoutException("Navigation response did not arrive"); }
    private static void Pump(int milliseconds)
    { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    private static void Capture(MainWindow window, string name)
    {
        var root = (FrameworkElement)window.Content; var bounds = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
        var visual = new DrawingVisual(); using (var draw = visual.RenderOpen()) draw.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds }, null, bounds);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var folder = Path.Combine(_root, "artifacts", "navigation"); Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(folder, name)); encoder.Save(output);
    }
    private static string Fingerprint() => File.Exists(SettingsService.SettingsPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SettingsService.SettingsPath))) : "missing";
    private static string FindRoot()
    { for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "src", "Embistic", "Embistic.csproj"))) return dir.FullName; throw new DirectoryNotFoundException(); }
    private static void LoadResources(Application app)
    {
        var xaml = XElement.Load(Path.Combine(_root, "src", "Embistic", "App.xaml")); XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary", new XAttribute("xmlns", ns.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:Embistic;assembly=Embistic"), new XAttribute(XNamespace.Xmlns + "controls", "clr-namespace:Embistic.Controls;assembly=Embistic"), xaml.Element(ns + "Application.Resources")!.Elements());
        foreach (var element in resources.Descendants().Where(e => e.Name.NamespaceName is "clr-namespace:Embistic" or "clr-namespace:Embistic.Controls")) element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Embistic");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }

    private sealed class LibraryServer : IDisposable
    {
        private readonly HttpListener _listener = new(); private readonly CancellationTokenSource _stop = new(); private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);
        public string Url { get; }
        public LibraryServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://127.0.0.1:{port}"; _listener.Prefixes.Add(Url + "/"); _listener.Start();
            _ = Task.Run(async () => { while (!_stop.IsCancellationRequested) try { var context = await _listener.GetContextAsync().WaitAsync(_stop.Token); _ = Respond(context); } catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { break; } });
        }
        private async Task Respond(HttpListenerContext context)
        {
            try
            {
                Interlocked.Increment(ref _requests);
                var query = context.Request.QueryString; var path = context.Request.Url!.AbsolutePath;
                var kind = query["IncludeItemTypes"]; var parent = query["ParentId"];
                var start = int.Parse(query["StartIndex"] ?? "0"); var limit = Math.Min(1000, int.Parse(query["Limit"] ?? "200"));
                object[] items; int total;
                if (path.EndsWith("/Views")) items = new object[] { new { Id = "music-root", Name = "音乐根目录", Type = "CollectionFolder", CollectionType = "music" } };
                else if (path.Contains("Artists")) items = new object[] { new { Id = "artist-1", Name = "演示艺术家", Type = "MusicArtist" } };
                else if (kind == "MusicAlbum") items = Enumerable.Range(start, Math.Min(limit, 18 - Math.Min(start, 18))).Select(i => (object)new { Id = "album-" + i, Name = "测试专辑 " + i, Artists = new[] { "演示艺术家" }, Type = "MusicAlbum" }).ToArray();
                else if (query["Recursive"] == "false")
                    items = parent == "music-root" ? Enumerable.Range(0, 30).Select(i => (object)new { Id = "folder-" + i, Name = "文件夹 " + i, Type = "Folder" }).ToArray()
                        : new object[] { new { Id = "album-child", Name = "子文件夹专辑", Type = "MusicAlbum" } };
                else
                {
                    total = 405;
                    var prefix = query["ArtistIds"] is not null ? "artist" : query["SearchTerm"] ?? parent ?? "all";
                    var album = parent ?? "album-target";
                    items = Enumerable.Range(start, Math.Max(0, Math.Min(limit, total - start))).Select(i => (object)new { Id = prefix + "-" + i, Name = "演示歌曲 " + i, Album = "上下文专辑", AlbumId = album, Type = "Audio", Artists = new[] { "演示艺术家" }, RunTimeTicks = 1800000000L }).ToArray();
                    await Write(context, new { Items = items, TotalRecordCount = total }); return;
                }
                total = items.Length; await Write(context, new { Items = items, TotalRecordCount = total });
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }
        private async Task Write(HttpListenerContext context, object payload)
        { var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)); context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes, _stop.Token); }
        public void Dispose() { _stop.Cancel(); _listener.Close(); _stop.Dispose(); }
    }
}
