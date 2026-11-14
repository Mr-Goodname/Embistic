using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private static readonly string Temp = Path.Combine(Path.GetTempPath(), "Embistic-playlists-" + Guid.NewGuid().ToString("N"));
    [STAThread]
    private static int Main(string[] args)
    {
        Directory.CreateDirectory(Temp);
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        LoadResources(app);
        try
        {
            if (args.Length >= 2 && args[0] == "--exit-save") return CheckExitSave(app, args[1]);
            CheckStorage(); CheckCursor();
            var work = CheckWindowAsync(args.Contains("--screenshots"));
            var deadline = DateTime.UtcNow.AddSeconds(50);
            while (!work.IsCompleted && DateTime.UtcNow < deadline) Pump(15);
            if (!work.IsCompleted) throw new TimeoutException("Playlist fixture did not complete");
            work.GetAwaiter().GetResult();
            CheckNativeDrag(args.Contains("--stress-native-input"));
            var exitWork = CheckExitSaveProcessAsync();
            while (!exitWork.IsCompleted) Pump(15);
            exitWork.GetAwaiter().GetResult();
            Console.WriteLine($"Completed: {_checks} playlist and queue checks passed."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex); return 1; }
        finally { app.Shutdown(); if (Path.GetFullPath(Temp).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(Temp, true); }
    }
    private static Track Song(string id) => new() { Id = id, Title = "歌曲 " + id, Artist = "Embistic 演示", Album = "夜航", AlbumId = "fixture-album", DurationMs = 210000, Container = "flac", ImageUrl = "https://fixture.invalid/cover?api_key=private-fixture-token", Path = "https://fixture.invalid/audio?token=private-fixture-token" };
    private static void CheckStorage()
    {
        var settings = new AppSettings { ServerUrl = "https://emby.invalid", UserId = "alice", ApiKey = "private-fixture-token" };
        var directory = Path.Combine(Temp, "saved");
        var store = new LocalPlaylistService(settings, directory);
        var a = Song("a"); var b = Song("b");
        var first = store.Create("夜晚", [a, b, a]);
        var second = store.Create("通勤", []);
        Check(store.GetAll().Count == 2 && first.Tracks.Select(x => x.Id).SequenceEqual(new[] { "a", "b", "a" }), "Multiple named lists preserve order and intentional duplicate entries");
        var json = File.ReadAllText(store.StoragePath);
        Check(!json.Contains("private-fixture-token") && !json.Contains("https://") && !json.Contains("ImageUrl") && !json.Contains("Path"), "Stored playlists contain no credentials, image URLs, stream URLs or local paths");
        store.AddTracks(second.Id, [b]); store.Rename(second.Id, "上班路上");
        Check(new LocalPlaylistService(settings, directory).GetAll().Single(x => x.Id == second.Id) is { Name: "上班路上", Tracks.Count: 1 }, "Add and rename survive reopening the local store");
        store.ReplaceTracks(first.Id, [b, a]);
        Check(store.GetAll().Single(x => x.Id == first.Id).Tracks.Select(x => x.Id).SequenceEqual(new[] { "b", "a" }), "Updating a selected list stores the edited queue order");
        store.GetAll().First().Tracks.Clear();
        Check(store.GetAll().All(x => x.Tracks.Count > 0), "Returned playlist snapshots cannot mutate the stored lists");
        var radio = new Track { Id = "radio:fixture", Path = "https://radio.invalid/token", Title = "电台" };
        Check(store.Create("只存歌曲", [radio, a]).Tracks.Count == 1, "Radio stream URLs are not persisted as Emby playlist tracks");
        var bob = new LocalPlaylistService(new AppSettings { ServerUrl = settings.ServerUrl, UserId = "bob" }, directory);
        var elsewhere = new LocalPlaylistService(new AppSettings { ServerUrl = "https://other.invalid", UserId = "alice" }, directory);
        Check(bob.GetAll().Count == 0 && elsewhere.GetAll().Count == 0 && store.StoragePath != bob.StoragePath && store.StoragePath != elsewhere.StoragePath, "Server and account boundaries isolate all local playlists");
        Check(new LocalPlaylistService(new AppSettings { ServerUrl = "https://EMBY.invalid:443/", UserId = "alice" }, directory).StoragePath == store.StoragePath, "Equivalent server URLs select the same account store");
        Fails(() => store.Create("夜晚", []), "Duplicate list names are rejected without overwriting another list");
        store.Delete(second.Id);
        Check(new LocalPlaylistService(settings, directory).GetAll().All(x => x.Id != second.Id), "Deleting one list preserves every other list");
        var original = File.ReadAllText(store.StoragePath); var backup = store.StoragePath + ".backup";
        File.Move(store.StoragePath, backup); Directory.CreateDirectory(store.StoragePath);
        Fails(() => store.Rename(first.Id, "未落盘的新名"), "A failed atomic replacement reports failure");
        Check(store.GetAll().Single(x => x.Id == first.Id).Name == "夜晚", "Failed disk writes leave the in-memory playlist unchanged");
        Directory.Delete(store.StoragePath); File.Move(backup, store.StoragePath);
        Check(File.ReadAllText(store.StoragePath) == original, "Failed writes preserve the previous complete file");
        var broken = new LocalPlaylistService(new AppSettings { ServerUrl = "https://broken.invalid", UserId = "alice" }, directory);
        File.WriteAllText(broken.StoragePath, "{broken"); broken = new LocalPlaylistService(new AppSettings { ServerUrl = "https://broken.invalid", UserId = "alice" }, directory);
        Fails(() => broken.Create("新建", []), "Malformed existing storage is preserved instead of silently overwritten");
    }
    private static void CheckCursor()
    {
        var position = QueueEditMath.Remove(1, false, 1);
        Check(position == (0, true), "Removing the current row creates a gap before its old successor");
        Check(QueueEditMath.Move(2, false, 0, 3, 4) == 1 && QueueEditMath.Move(1, false, 1, 3, 4) == 3, "Moves preserve the active occurrence even when other entries cross it");
        Check(QueueEditMath.Move(0, true, 1, 3, 4) == 2, "Detached cursor follows its next occurrence through reordering");
        Check(QueueEditMath.Remove(-1, true, 0) == (-1, true), "Removing the next detached song advances naturally to the following entry");
    }
    private static async Task CheckWindowAsync(bool screenshots)
    {
        using var server = new AlbumServer();
        var window = new MainWindow();
        window.Loaded -= (RoutedEventHandler)Method(window, "Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), window);
        window.SourceInitialized -= (EventHandler)Method(window, "Window_SourceInitialized").CreateDelegate(typeof(EventHandler), window);
        StopTimers(window); Set(window, "_saveSettings", new Action<AppSettings>(_ => { })); Set(window, "_history", null);
        (Get<object?>(window, "_downloads") as IDisposable)?.Dispose(); Set(window, "_downloads", null);
        var settings = new AppSettings { ServerUrl = server.Url, UserId = "fixture-user", ApiKey = "fixture-token", Theme = new ThemeSettings { Mode = "Dark" }, CheckUpdatesAutomatically = false };
        Set(window, "_settings", settings);
        var store = new LocalPlaylistService(settings, Path.Combine(Temp, "window")); Set(window, "_localPlaylists", store);
        var client = new EmbyClient(settings); Set(window, "_emby", client);
        Get<ThemeService>(window, "_theme").Apply(settings.Theme);
        window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000; window.Width = 1380; window.Height = 900; window.Opacity = 0;
        try
        {
            window.Show(); Pump(100);
            Check(!Get<bool>(window, "_ready") && Get<object?>(window, "_player") is null, "Queue fixture never initializes an audio engine or logs into a real account");
            var a = Song("a"); var b = Song("b"); var c = Song("c");
            foreach (var item in new[] { a, b, c }) item.ImageUrl = "";
            Set(window, "_current", b); Set(window, "_playing", true); Set(window, "_position", 12345L); Invoke(window, "NotifyNowPlaying");
            Invoke(window, "ReplaceQueueWithoutPlayback", new[] { a, b, c }, "演示队列");
            Invoke(window, "MoveQueueEntry", 0, 2);
            Check(window.Queue.SequenceEqual(new[] { b, c, a }) && Get<int>(window, "_queueIndex") == 0, "Real queue reordering follows the playing row without changing the music");
            Invoke(window, "RemoveQueueAt", 0);
            Check(window.Current == b && Get<bool>(window, "_playing") && Get<long>(window, "_position") == 12345 && Get<bool>(window, "_queueDetached") && window.Queue[Get<int>(window, "_queueIndex") + 1] == c,
                "Removing the current song keeps audio state and makes its old successor next");
            Invoke(window, "MoveQueueEntry", 1, 0);
            Check(window.Queue[Get<int>(window, "_queueIndex") + 1] == c, "Reordering after removing the current song preserves the next-song gap");
            Invoke(window, "ClearQueue_Click", window, new RoutedEventArgs());
            await (Task)Invoke(window, "AdvanceAsync", false)!;
            Check(window.Queue.Count == 0 && window.Current == b && Get<bool>(window, "_playing") && Get<long>(window, "_position") == 12345, "Clearing the queue and pressing next never stops independent current playback");
            var saved = store.Create("通勤收藏", [a, c]);
            Invoke(window, "LoadLocalPlaylist", saved.Id);
            Check(window.Queue.Select(x => x.Id).SequenceEqual(new[] { "a", "c" }) && window.Current == b && Get<int>(window, "_queueIndex") == -1,
                "Loading a saved list replaces upcoming songs without restarting the current track");
            var popup = Find<Popup>(window, "QueuePopup"); popup.Child.Opacity = 0;
            Find<Button>(window, "QueuePopupButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(popup.IsOpen && Get<string>(window, "_section") == "home", "Bottom playlist button opens the queue without leaving the current page");
            var queueView = Find<QueuePopupView>(window, "QueuePopupContent");
            var queueItems = Find<ListBox>(queueView, "QueueItems");
            var moved = 0; var played = -1;
            queueView.MoveRequested += (_, _) => moved++;
            queueView.PlayRequested += index => played = index;
            var firstRow = (ListBoxItem)queueItems.ItemContainerGenerator.ContainerFromIndex(0)!;
            var down = Descendants<Button>(firstRow).Single(x => x.ToolTip as string == "下移");
            down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(moved == 1 && window.Queue[1].Id == "a", "Popup row move controls edit the real queue");
            var nameInput = Find<TextBox>(queueView, "PlaylistName");
            var nameBefore = window.Current;
            popup.IsOpen = false;
            var snapshotPosition = Get<long>(window, "_position");
            await (Task)Invoke(window, "OpenAlbumAsync", new Album { Id = "large", Title = "完整专辑" })!;
            Check(window.Queue.Count == 1105 && window.Queue[0].Id == "large-0" && window.Queue[^1].Id == "large-1104", "Opening an album loads all pages beyond the 200-row display and server page cap");
            Check(server.StartsFor("large").Contains(900) && window.Tracks.Count == 200, "Full album queue follows actual returned page sizes while the library stays paged");
            Check(window.Current == nameBefore && Get<bool>(window, "_playing") && Get<long>(window, "_position") == snapshotPosition && Get<int>(window, "_queueIndex") == -1,
                "Opening a complete album leaves the current track, position and playing state unchanged");
            try { await (Task)Invoke(window, "PlayVisibleTrackAsync", window.Tracks[17])!; }
            catch (InvalidOperationException) { /* Fixture has no audio engine; selecting the queue occurrence still runs before engine startup. */ }
            Check(window.Queue.Count == 1105 && Get<int>(window, "_queueIndex") == 17 && window.Current?.Id == "large-17", "Playing a visible album row preserves the complete album queue");
            Set(window, "_current", b); Set(window, "_playing", true); Set(window, "_position", 12345L); Invoke(window, "NotifyNowPlaying");
            var slow = (Task)Invoke(window, "OpenAlbumAsync", new Album { Id = "slow", Title = "较慢的专辑" })!;
            await Task.Delay(40);
            await (Task)Invoke(window, "OpenAlbumAsync", new Album { Id = "quick", Title = "新的专辑" })!;
            try { await slow; } catch (OperationCanceledException) { }
            Check(window.Queue.Count == 4 && window.Queue.All(x => x.Id.StartsWith("quick-")), "A late response from an older album cannot overwrite a newer album selection");
            var pending = (Task)Invoke(window, "OpenAlbumAsync", new Album { Id = "slow", Title = "慢专辑" })!;
            await Task.Delay(40); window.Queue.Add(a); await pending;
            Check(window.Queue[^1] == a && window.Queue.Any(x => x.Id.StartsWith("quick-")), "A delayed album load cannot overwrite manual queue edits made while waiting");
            var enteringLyrics = (Task)Invoke(window, "OpenAlbumAsync", new Album { Id = "slow", Title = "慢专辑" })!;
            await Task.Delay(40); Invoke(window, "ShowPage", "lyrics"); await enteringLyrics;
            Check(window.Queue.Count == 505 && window.Queue.All(x => x.Id.StartsWith("slow-")), "Entering immersive lyrics during an album request does not cancel the complete album queue");
            if (screenshots)
            {
                b.Title = "夜航"; Set(window, "_current", b); Invoke(window, "NotifyNowPlaying");
                Invoke(window, "ReplaceQueueWithoutPlayback", Enumerable.Range(1, 18).Select(i => new Track { Id = "demo-" + i, Title = new[] { "夜航", "晚风经过窗边", "城市里的星光", "回家的路" }[(i - 1) % 4], Artist = "Embistic 演示", DurationMs = 180000 }).ToArray(), "专辑 · 夜航");
                Invoke(window, "RefreshLocalPlaylists", (object?)null);
                queueView.Opacity = 1; popup.IsOpen = true; Pump(120);
                SaveScreenshot(queueView, Path.Combine(FindRoot(), "docs", "images", "queue.png")); popup.IsOpen = false;
            }
        }
        finally
        {
            StopTimers(window); Set(window, "_closing", true); Set(window, "_allowClose", true);
            foreach (var field in new[] { "_spectrumService", "_theme", "_downloads", "_dlna", "_lyrics", "_emby" }) (Get<object?>(window, field) as IDisposable)?.Dispose();
            window.Close();
        }
    }
    private static void CheckNativeDrag(bool stressInput)
    {
        GetCursorPos(out var originalCursor); var foreground = GetForegroundWindow();
        var tracks = new ObservableCollection<Track>(Enumerable.Range(0, 5).Select(i => new Track { Id = "native-" + i, Title = "拖动检查 " + i, Artist = "Embistic" }));
        var view = new QueuePopupView(); view.SetQueue(tracks);
        var moved = 0; view.MoveRequested += (from, to) => { tracks.Move(from, to); moved++; };
        var gate = new object();
        var moves = 0; var downs = 0; var ups = 0; var dragEnters = 0; var dragOvers = 0; var drops = 0;
        var dragTarget = -1; var dragOverTarget = -1;
        var routedDowns = 0; var routedHandleDowns = 0; var sourceQueries = 0; var sourceFeedback = 0;
        var lastPointer = new Point(); var lastSource = "none"; var downSource = "none"; var downTag = "none";
        Point? requestedPointer = null;
        var pointerRequest = 0; var pointerReceipt = 0;
        var host = new Window { Content = view, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Left = SystemParameters.WorkArea.Left + 30, Top = SystemParameters.WorkArea.Top + 30,
            Opacity = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ? 1 : .04 };
        try
        {
            var list = Find<ListBox>(view, "QueueItems");
            host.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler((_, e) =>
            {
                lock (gate)
                {
                    moves++; lastPointer = host.PointToScreen(e.GetPosition(host)); lastSource = e.OriginalSource.GetType().Name;
                    // Preserve the matching routed event as an acknowledgement. A
                    // later hover/layout event must not erase an already delivered move.
                    if (requestedPointer is { } expected && (lastPointer - expected).Length <= 1) pointerReceipt = pointerRequest;
                }
            }), true);
            host.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, e) =>
            { lock (gate) { downs++; lastSource = downSource = e.OriginalSource.GetType().Name; } }), true);
            host.AddHandler(Mouse.MouseDownEvent, new MouseButtonEventHandler((_, e) =>
            {
                if (e.ChangedButton != MouseButton.Left) return;
                var element = e.OriginalSource as DependencyObject;
                while (element is not null && element is not FrameworkElement)
                    element = LogicalTreeHelper.GetParent(element);
                var hitHandle = element is FrameworkElement { Tag: "QueueDragHandle" };
                lock (gate) downTag = (element as FrameworkElement)?.Tag?.ToString() ?? "none";
                // The source control records its drag origin in PreviewMouseDown.
                // A preview count at the host is too early: moving the OS cursor
                // there can change GetPosition before the source finishes routing.
                host.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    lock (gate) { routedDowns++; if (hitHandle) routedHandleDowns++; }
                });
            }), true);
            host.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => { lock (gate) ups++; }), true);
            list.AddHandler(DragDrop.QueryContinueDragEvent, new QueryContinueDragEventHandler((_, _) => { lock (gate) sourceQueries++; }), true);
            list.AddHandler(DragDrop.GiveFeedbackEvent, new GiveFeedbackEventHandler((_, _) => { lock (gate) sourceFeedback++; }), true);
            list.AddHandler(DragDrop.DragEnterEvent, new DragEventHandler((_, e) =>
            {
                var row = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject);
                lock (gate) { dragEnters++; dragTarget = row is null ? -1 : list.ItemContainerGenerator.IndexFromContainer(row); }
            }), true);
            list.AddHandler(DragDrop.DragOverEvent, new DragEventHandler((_, e) =>
            {
                var row = ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject);
                lock (gate) { dragOvers++; dragTarget = dragOverTarget = row is null ? -1 : list.ItemContainerGenerator.IndexFromContainer(row); }
            }), true);
            list.AddHandler(DragDrop.DropEvent, new DragEventHandler((_, _) => { lock (gate) drops++; }), true);
            if (stressInput)
                list.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => Thread.Sleep(250)), true);
            var contentRendered = false;
            host.ContentRendered += (_, _) => contentRendered = true;
            host.Show(); host.UpdateLayout();
            var renderDeadline = Stopwatch.StartNew();
            while (!contentRendered && renderDeadline.ElapsedMilliseconds < 2000) Pump(5);
            if (!contentRendered) throw new InvalidOperationException("Native queue fixture did not finish its first rendered layout");
            host.UpdateLayout();
            var hwnd = new WindowInteropHelper(host).Handle;
            var initiallyForeground = GetForegroundWindow() == hwnd;
            var uiDpiContext = GetThreadDpiAwarenessContext();
            GetWindowRect(hwnd, out var uiBounds);
            Console.WriteLine($"NATIVE QUEUE UI: context={uiDpiContext}, awareness={GetAwarenessFromDpiAwarenessContext(uiDpiContext)}, windowDpi={GetDpiForWindow(hwnd)}, bounds={uiBounds.Left},{uiBounds.Top},{uiBounds.Right},{uiBounds.Bottom}");
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)!;
            var third = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(2)!;
            var handle = Descendants<TextBlock>(first).Single(x => x.Tag as string == "QueueDragHandle");
            var origin = handle.PointToScreen(new Point(handle.ActualWidth * .5, handle.ActualHeight * .5));
            var destination = third.PointToScreen(new Point(80, third.ActualHeight * .5));
            var activation = host.PointToScreen(new Point(8, 8));
            async Task WaitInput(Func<bool> received, string stage)
            {
                Console.WriteLine("NATIVE QUEUE WAIT: " + stage);
                var deadline = Stopwatch.StartNew();
                bool Ready() { lock (gate) return received(); }
                while (!Ready() && deadline.ElapsedMilliseconds < 2000) await Task.Delay(5);
                if (!Ready())
                {
                    GetCursorPos(out var win32); GetPhysicalCursorPos(out var physical); GetWindowRect(hwnd, out var bounds);
                    var context = GetThreadDpiAwarenessContext();
                    lock (gate) throw new InvalidOperationException($"Native queue fixture {stage}; foreground={GetForegroundWindow() == hwnd}, moves={moves}, downs={downs}, routedDowns={routedDowns}, routedHandleDowns={routedHandleDowns}, downSource={downSource}, downTag={downTag}, ups={ups}, sourceQueries={sourceQueries}, sourceFeedback={sourceFeedback}, dragEnters={dragEnters}, dragOvers={dragOvers}, drops={drops}, target={dragTarget}, overTarget={dragOverTarget}, source={lastSource}, pointer={lastPointer}, receipt={pointerReceipt}/{pointerRequest}, win32={win32.X},{win32.Y}, physical={physical.X},{physical.Y}, context={context}, awareness={GetAwarenessFromDpiAwarenessContext(context)}, dpi={GetDpiForWindow(hwnd)}, bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}");
                }
                Console.WriteLine("NATIVE QUEUE ACK: " + stage);
            }
            async Task MoveAndWait(Point target)
            {
                var rounded = new Point(Math.Round(target.X), Math.Round(target.Y));
                GetCursorPos(out var before); int request;
                lock (gate) { request = ++pointerRequest; requestedPointer = rounded; }
                var context = GetThreadDpiAwarenessContext();
                if (!SetCursorPos((int)rounded.X, (int)rounded.Y)) throw new InvalidOperationException("Native cursor movement was rejected");
                GetCursorPos(out var after); GetPhysicalCursorPos(out var physical);
                Console.WriteLine($"NATIVE QUEUE MOVE: expected={rounded}, win32={after.X},{after.Y}, physical={physical.X},{physical.Y}, context={context}, awareness={GetAwarenessFromDpiAwarenessContext(context)}");
                if (before.X != rounded.X || before.Y != rounded.Y)
                    await WaitInput(() => pointerReceipt == request, $"pointer delivery (expected={rounded})");
            }
            async Task MoveDragPath(Point from, Point to, int steps)
            {
                // Continuous native motion also produces target notifications when
                // OLE does not synthesize another event for a stationary pointer.
                for (var step = 1; step <= steps; step++)
                {
                    var fraction = step / (double)steps;
                    if (!SetCursorPos((int)Math.Round(from.X + (to.X - from.X) * fraction), (int)Math.Round(from.Y + (to.Y - from.Y) * fraction)))
                        throw new InvalidOperationException("Native drag path movement was rejected");
                    await Task.Delay(16);
                }
            }
            var input = Task.Run(async () =>
            {
                try
                {
                    // A native padding click grants foreground input even when Windows
                    // rejects a background process's SetForegroundWindow request.
                    await MoveAndWait(activation);
                    SendMouse(0x0002); await WaitInput(() => routedDowns >= 1, "activation routed down");
                    SendMouse(0x0004); await WaitInput(() => ups >= 1 && GetForegroundWindow() == hwnd, "activation up / foreground");
                    await MoveAndWait(origin);
                    SendMouse(0x0002); await WaitInput(() => routedHandleDowns >= 1, "handle routed down / actual handle hit");
                    Console.WriteLine($"NATIVE QUEUE DOWN: source={downSource}, tag={downTag}, routedHandleDowns={routedHandleDowns}");
                    var threshold = new Point((int)origin.X + 15, (int)origin.Y + 12);
                    if (!SetCursorPos((int)threshold.X, (int)threshold.Y)) throw new InvalidOperationException("Native drag-start movement was rejected");
                    // Source-side OLE callbacks prove DoDragDrop is running without
                    // waiting for a target notification that itself needs more motion.
                    await WaitInput(() => sourceQueries > 0 || sourceFeedback > 0, "source OLE QueryContinueDrag / GiveFeedback");
                    var approach = new Point((int)destination.X - 8, (int)destination.Y);
                    await MoveDragPath(threshold, approach, 6);
                    await WaitInput(() => dragTarget == 2, "destination enter");
                    // Crossing into another row can itself raise DragEnter; move
                    // within that row before requiring the real target DragOver.
                    await MoveDragPath(approach, destination, 4);
                    await WaitInput(() => dragOverTarget == 2, "destination DragOver");
                    SendMouse(0x0004); await WaitInput(() => drops == 1, "Drop");
                }
                catch (Exception ex)
                {
                    // Print from the driver: the UI may still be in DoDragDrop's
                    // nested OLE loop and unable to observe the faulted task yet.
                    Console.Error.WriteLine("NATIVE QUEUE DRIVER FAILED: " + ex);
                    throw;
                }
                finally
                {
                    // This must run on the background driver, not only in the UI's
                    // outer finally. Mouse-up releases a failed OLE drag so the UI
                    // can leave its nested loop and report the original failure.
                    SendMouse(0x0004);
                    Console.WriteLine("NATIVE QUEUE DRIVER CLEANUP: mouse released");
                }
            });
            while (!input.IsCompleted) Pump(15);
            input.GetAwaiter().GetResult();
            Console.WriteLine($"NATIVE QUEUE: initialForeground={initiallyForeground}, busyUiMs={(stressInput ? 250 : 0)}, moves={moves}, downs={downs}, routedHandleDowns={routedHandleDowns}, downSource={downSource}, downTag={downTag}, ups={ups}, sourceQueries={sourceQueries}, sourceFeedback={sourceFeedback}, dragEnters={dragEnters}, dragOvers={dragOvers}, drops={drops}, target={dragTarget}, overTarget={dragOverTarget}");
            Check(moved == 1 && tracks.Select(x => x.Id).SequenceEqual(new[] { "native-1", "native-2", "native-0", "native-3", "native-4" }),
                "Real Windows mouse press/move/drop on the queue handle reorders the native WPF list");
        }
        finally
        {
            SendMouse(0x0004); Mouse.Capture(null); host.Close(); SetCursorPos(originalCursor.X, originalCursor.Y);
            if (foreground != IntPtr.Zero) SetForegroundWindow(foreground);
        }
    }
    private static async Task CheckExitSaveProcessAsync()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--exit-save"); start.ArgumentList.Add(Path.Combine(Temp, "exit-store"));
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var transcript = await output; var failure = await errors;
        Check(child.ExitCode == 0 && transcript.Contains("EXIT_SAVE_PASSED"), "Immediate application exit waits for an in-flight playlist write before shutdown: " + failure);
    }
    private static int CheckExitSave(Application app, string directory)
    {
        var window = new MainWindow();
        window.Loaded -= (RoutedEventHandler)Method(window, "Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), window);
        window.SourceInitialized -= (EventHandler)Method(window, "Window_SourceInitialized").CreateDelegate(typeof(EventHandler), window);
        StopTimers(window); Set(window, "_saveSettings", new Action<AppSettings>(_ => { })); Set(window, "_history", null);
        (Get<object?>(window, "_downloads") as IDisposable)?.Dispose(); Set(window, "_downloads", null);
        var settings = new AppSettings { ServerUrl = "https://exit-fixture.invalid", UserId = "exit-user", CloseToTray = false };
        Set(window, "_settings", settings); var store = new LocalPlaylistService(settings, directory); Set(window, "_localPlaylists", store);
        window.ShowActivated = false; window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000; window.Opacity = 0;
        window.Loaded += (_, _) =>
        {
            Method(window, "RunPlaylistWriteAsync").MakeGenericMethod(typeof(int)).Invoke(window, [new Func<int>(() => { Thread.Sleep(350); store.Create("退出前保存", [Song("exit-song")]); return 1; })]);
            window.Close();
        };
        app.Run(window);
        if (new LocalPlaylistService(settings, directory).GetAll().SingleOrDefault()?.Tracks.SingleOrDefault()?.Id != "exit-song") return 1;
        Console.WriteLine("EXIT_SAVE_PASSED"); return 0;
    }
    private static void SendMouse(uint flags)
    {
        var input = new MouseInput { Flags = flags }; var item = new NativeInput { Mouse = input };
        if (SendInput(1, [item], Marshal.SizeOf<NativeInput>()) != 1) throw new InvalidOperationException("Native mouse input was rejected");
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    private sealed class AlbumServer : IDisposable
    {
        private readonly HttpListener _listener = new(); private readonly CancellationTokenSource _stop = new();
        private readonly System.Collections.Concurrent.ConcurrentBag<(string Album, int Start)> _requests = [];
        public string Url { get; }
        public IEnumerable<int> StartsFor(string album) => _requests.Where(x => x.Album == album).Select(x => x.Start);
        public AlbumServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Url = $"http://127.0.0.1:{port}"; _listener.Prefixes.Add(Url + "/"); _listener.Start();
            _ = Task.Run(async () => { while (!_stop.IsCancellationRequested) { try { var context = await _listener.GetContextAsync().WaitAsync(_stop.Token); _ = Respond(context); } catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { break; } } });
        }
        private async Task Respond(HttpListenerContext context)
        {
            try
            {
                var album = context.Request.QueryString["ParentId"] ?? "none"; var start = int.Parse(context.Request.QueryString["StartIndex"] ?? "0"); var limit = Math.Min(300, int.Parse(context.Request.QueryString["Limit"] ?? "200"));
                _requests.Add((album, start));
                if (album == "slow") await Task.Delay(300, _stop.Token);
                var total = album == "large" ? 1105 : album == "slow" ? 505 : 4;
                var items = Enumerable.Range(start, Math.Max(0, Math.Min(limit, total - start))).Select(i => new { Id = album + "-" + i, Name = "专辑曲目 " + i, Album = "演示专辑", AlbumId = album, Type = "Audio", RunTimeTicks = 1800000000L, Artists = new[] { "Embistic 演示" } });
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Items = items, TotalRecordCount = total }));
                context.Response.ContentType = "application/json"; context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }
        public void Dispose() { _stop.Cancel(); _listener.Close(); _stop.Dispose(); }
    }
    private static void Check(bool okay, string label) { if (!okay) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); _checks++; }
    private static void Fails(Action action, string label) { try { action(); } catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException) { Check(true, label); return; } throw new InvalidOperationException(label); }
    private static T Find<T>(FrameworkElement root, string name) where T : class => (T)(root.FindName(name) ?? throw new InvalidOperationException(name));
    private static MethodInfo Method(object obj, string name) => obj.GetType().GetMethod(name, Private)!;
    private static object? Invoke(object obj, string name, params object?[] args) => Method(obj, name).Invoke(obj, args);
    private static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, Private)!.GetValue(obj)!;
    private static void Set(object obj, string name, object? value) => obj.GetType().GetField(name, Private)!.SetValue(obj, value);
    private static void StopTimers(object window) { foreach (var field in window.GetType().GetFields(Private)) if (field.GetValue(window) is DispatcherTimer timer) timer.Stop(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var next in Descendants<T>(child)) yield return next; } }
    private static void Pump(int milliseconds = 35) { var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) }; timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame); }
    private static string FindRoot() { for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "src", "Embistic", "App.xaml"))) return directory.FullName; throw new DirectoryNotFoundException(); }
    private static void LoadResources(Application app)
    {
        var appXaml = XElement.Load(Path.Combine(FindRoot(), "src", "Embistic", "App.xaml")); XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(ns + "ResourceDictionary", new XAttribute("xmlns", ns.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:Embistic;assembly=Embistic"), new XAttribute(XNamespace.Xmlns + "controls", "clr-namespace:Embistic.Controls;assembly=Embistic"), appXaml.Element(ns + "Application.Resources")!.Elements());
        foreach (var element in resources.Descendants().Where(x => x.Name.NamespaceName is "clr-namespace:Embistic" or "clr-namespace:Embistic.Controls")) element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Embistic");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
    private static void SaveScreenshot(FrameworkElement view, string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var image = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
}
