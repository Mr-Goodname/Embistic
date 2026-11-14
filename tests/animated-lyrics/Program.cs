using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Embistic.Controls;
using Embistic.Models;

internal static class Program
{
    private static int _passed;
    [STAThread]
    private static int Main()
    {
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            Window? window = null;
            try
            {
                if (Environment.GetCommandLineArgs().Contains("--performance"))
                {
                    window = await MeasurePlaybackAsync();
                    return;
                }
                Check(LyricScrollMath.CenterOffset(800, 100, 500, 2000) == 600, "Centers a measured line in the viewport");
                Check(LyricScrollMath.CenterOffset(800, 240, 500, 2000) == 670, "Wrapped translations contribute to the center");
                Check(LyricScrollMath.CenterOffset(0, 100, 500, 2000) == 0, "Clamps the leading edge");
                Check(LyricScrollMath.CenterOffset(1800, 240, 500, 2000) == 1500, "Clamps the trailing edge");
                Check(LyricScrollMath.CenterOffset(double.NaN, 100, 500, 2000) == 0, "Rejects invalid layout values");
                Check(!LyricScrollMath.IsInViewport(0, 100, 200, 500) && LyricScrollMath.IsInViewport(150, 100, 200, 500), "Only intersecting rows qualify for animations");
                var scrollTime = new ControlledScrollTime();
                var view = new AnimatedLyricsView(scrollTime);
                view.ApplyAppearance(new LyricAppearanceSettings { ScrollDurationMs = 1200, TransitionDurationMs = 1000 });
                window = new Window { Content = view, Width = 640, Height = 600, Left = SystemParameters.WorkArea.Right - 650, Top = SystemParameters.WorkArea.Bottom - 610, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false, Opacity = .01, Background = new SolidColorBrush(Color.FromRgb(12, 16, 21)) };
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.Loaded);
                var lines = Enumerable.Range(0, 24).Select(index => new LyricsLine(TimeSpan.FromSeconds(index * 5), index == 2
                    ? "风经过漫长的夏天，把故事轻轻留在你我之间，在每一个安静而温柔的夜晚\nThe wind carries our stories through the long summer and leaves them gently between us.\n風は長い夏を通り抜け、私たちの物語をそっと運んでゆく。"
                    : $"这是第 {index + 1} 句歌词，沿着旋律慢慢向前\nA quiet moment in the melody, line {index + 1}.")).ToArray();
                view.SetDocument(new LyricsDocument { IsSynced = true, Lines = lines, Source = "Animation fixture" });
                view.UpdatePosition(0);
                await Task.Delay(180);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                scrollTime.Advance(TimeSpan.FromMilliseconds(1200));
                await NextRenderFrameAsync(view);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var rows = (StackPanel)view.FindName("RowsPanel");
                var scroller = (ScrollViewer)view.FindName("Scroller");
                Check(rows.Children.Count == 24 && ((Button)rows.Children[2]).ActualHeight > ((Button)rows.Children[0]).ActualHeight, "Rows keep natural multi-line translation heights");
                var before = view.CurrentScrollOffset;
                var nativeBefore = scroller.VerticalOffset;
                var rowBefore = ((Button)rows.Children[2]).TranslatePoint(new Point(), view).Y;
                var scrollStarted = scrollTime.GetTimestamp();
                view.UpdatePosition(10000);
                var target = view.TargetScrollOffset;
                Check(view.ActiveLineIndex == 2 && target > before + 100, "New active line targets its measured center");
                Check(Math.Abs(view.CurrentScrollOffset - before) < 8, "A transition does not jump to its destination immediately");
                scrollTime.Advance(TimeSpan.FromMilliseconds(160));
                await NextRenderFrameAsync(view);
                await Task.Delay(160);
                Check(view.CurrentScrollOffset > before + 1 && view.CurrentScrollOffset < target - 1, $"Scroll offset progresses through intermediate values (from={before:F1}, current={view.CurrentScrollOffset:F1}, to={target:F1})");
                Check(Math.Abs(scroller.VerticalOffset - nativeBefore) < .1 &&
                    Math.Abs(((Button)rows.Children[2]).TranslatePoint(new Point(), view).Y - rowBefore + view.CurrentScrollOffset - before) < 1,
                    "Intermediate frames move the actual lyric visuals without requesting native scroll layout");
                var focusedContent = (StackPanel)((Button)rows.Children[2]).Content;
                var focusedMain = (OutlinedLyricsText)focusedContent.Children[0];
                Check(focusedMain.Opacity > .72 && focusedMain.Opacity < 1, "Active lyric opacity transitions through intermediate values");
                window.Height += 10;
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(Math.Abs(view.TargetScrollOffset - target) < 1 && view.CurrentScrollOffset < target - 1,
                    $"Late height layout preserves the active scroll instead of snapping (current={view.CurrentScrollOffset:F1}, target={target:F1})");
                scrollTime.Advance(TimeSpan.FromMilliseconds(400));
                await NextRenderFrameAsync(view);
                var beforeWidthLayout = view.CurrentScrollOffset;
                var reflowWallClock = Stopwatch.StartNew();
                window.Width = 540;
                if (Environment.GetCommandLineArgs().Contains("--slow-layout")) Thread.Sleep(2600);
                window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var reflowTarget = view.TargetScrollOffset;
                Console.WriteLine($"Layout fixture: WPF reflow wall time={reflowWallClock.Elapsed.TotalMilliseconds:F0}ms; controlled scroll time={scrollTime.GetElapsedTime(scrollStarted).TotalMilliseconds:F0}ms of the original 1200ms deadline.");
                Check(Math.Abs(reflowTarget - target) > 1 && view.CurrentScrollOffset < reflowTarget - 1 && view.CurrentScrollOffset >= beforeWidthLayout - 2,
                    $"Late width layout retargets the wrapped line while retaining an intermediate scroll position (before={beforeWidthLayout:F1}, current={view.CurrentScrollOffset:F1}, oldTarget={target:F1}, target={reflowTarget:F1})");
                target = reflowTarget;
                view.UpdatePosition(10300);
                scrollTime.Advance(TimeSpan.FromMilliseconds(639));
                await NextRenderFrameAsync(view);
                var stillMovingBeforeDeadline = ScrollIsMoving(view);
                scrollTime.Advance(TimeSpan.FromMilliseconds(1));
                await NextRenderFrameAsync(view);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(stillMovingBeforeDeadline && !ScrollIsMoving(view) && Math.Abs(view.CurrentScrollOffset - target) < .1
                    && scrollTime.GetElapsedTime(scrollStarted) == TimeSpan.FromMilliseconds(1200),
                    "Layout retargeting remains animated at 1199ms and reaches the new destination at the original 1200ms deadline");
                var focusedScale = (ScaleTransform)((TransformGroup)focusedContent.RenderTransform).Children[0];
                await WaitUntilAsync(() => focusedMain.Opacity == 1 && Math.Abs(focusedScale.ScaleX - 1.18) < .001,
                    "Focus appearance did not complete its independent WPF animation");
                Check(Math.Abs(view.CurrentScrollOffset - target) < 2, "Repeated ticks on one lyric do not restart scrolling");
                Check(Math.Abs(scroller.VerticalOffset - target) < 2 && Math.Abs(((TranslateTransform)((StackPanel)view.FindName("LineHost")).RenderTransform).Y) < .1,
                    "Settled animation commits the native scroll offset and releases its render displacement");
                Check(Math.Abs(focusedScale.ScaleX - 1.18) < .001 && focusedMain.Opacity == 1, "Active lyric settles at the configured focus scale and opacity");
                var neighbor = (StackPanel)((Button)rows.Children[3]).Content;
                var distant = (StackPanel)((Button)rows.Children[6]).Content;
                var neighborScale = (ScaleTransform)((TransformGroup)neighbor.RenderTransform).Children[0];
                var distantScale = (ScaleTransform)((TransformGroup)distant.RenderTransform).Children[0];
                Check(neighborScale.ScaleX < focusedScale.ScaleX && distantScale.ScaleX < neighborScale.ScaleX, "Neighboring lyrics shrink progressively with distance from the playing line");
                var pendingRow = (Button)rows.Children[3];
                pendingRow.InvalidateArrange();
                Check(!pendingRow.IsArrangeValid, "The deferred-center fixture begins before the target row is arranged");
                view.UpdatePosition(15000);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(view.ActiveLineIndex == 3 && view.CurrentScrollOffset < view.TargetScrollOffset - 1,
                    "A playback transition requested before row layout keeps its animation intent");
                scrollTime.Resume();
                await WaitUntilAsync(() => Math.Abs(view.CurrentScrollOffset - view.TargetScrollOffset) < .1, "Deferred playback transition did not reach its measured destination");
                scroller.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                await Task.Delay(300);
                Check(!view.IsFollowingPlayback, "Mouse wheel suspends automatic following");
                // The runner can process the final WPF animation frame later than its
                // nominal duration. Snapshot the settled manual position, not a frame
                // still moving towards the wheel target.
                await WaitUntilAsync(() => Math.Abs(view.CurrentScrollOffset - view.TargetScrollOffset) < 2,
                    "Mouse wheel scrolling did not reach its destination");
                var manualOffset = view.CurrentScrollOffset;
                await Task.Delay(5250);
                Check(!view.IsFollowingPlayback && Math.Abs(view.CurrentScrollOffset - manualOffset) < 2,
                    $"Paused playback preserves manual reading position after five seconds (following={view.IsFollowingPlayback}, expected={manualOffset:F2}, actual={view.CurrentScrollOffset:F2})");
                view.UpdatePosition(10500);
                await WaitUntilAsync(() => view.IsFollowingPlayback && Math.Abs(view.CurrentScrollOffset - view.TargetScrollOffset) < 2, "Playback did not restore following after the manual hold");
                Check(view.IsFollowingPlayback && Math.Abs(view.CurrentScrollOffset - view.TargetScrollOffset) < 2, "Playback motion restores following after the manual hold");
                long? requested = null;
                view.SeekRequested += timestamp => requested = timestamp;
                var button = (Button)rows.Children[4];
                var peer = UIElementAutomationPeer.CreatePeerForElement(button)!;
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(requested == 20000, "UI Automation Invoke seeks to the original timestamp");
                requested = null;
                var enterButton = (Button)rows.Children[5];
                enterButton.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check(requested == 25000, "Enter activates a lyric without altering its timestamp");
                view.ApplyAppearance(new LyricAppearanceSettings { MainFontSize = 32, Alignment = "Left", AnimationsEnabled = false });
                view.UpdatePosition(15000, 5000);
                await Task.Delay(100);
                Check(view.ActiveLineIndex == 4 && Math.Abs(view.CurrentScrollOffset - view.TargetScrollOffset) < 2, "Offset selection and disabled-animation mode apply immediately");
                window.Width = 420;
                await Task.Delay(120);
                Check(Math.Abs(view.CurrentScrollOffset - view.TargetScrollOffset) < 2, "Resize recomputes centered offsets after wrapping");
                view.UpdatePlayback(12000, true);
                await Task.Delay(130);
                Check(view.InterpolatedPositionMs >= 12100, "Playback clock advances smoothly between audio position observations");
                var beforeRepeated = view.InterpolatedPositionMs;
                view.UpdatePlayback(12000, true);
                Check(view.InterpolatedPositionMs >= beforeRepeated, "Repeated coarse audio observations do not rewind the interpolated clock");
                await Task.Delay(1250);
                Check(view.InterpolatedPositionMs <= 13201, "Stale audio observations bound interpolation during buffering");
                view.UpdatePlayback(12000, false);
                await Task.Delay(120);
                Check(Math.Abs(view.InterpolatedPositionMs - 12000) < 1, "Paused playback freezes the interpolated lyric clock");
                view.UpdatePlayback(1000, false);
                Check(Math.Abs(view.InterpolatedPositionMs - 1000) < 1, "Seeking immediately rebases the playback clock");
                CheckRetainedKaraoke();
                if (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) is { } imagePath)
                {
                    view.ApplyAppearance(new LyricAppearanceSettings());
                    view.UpdatePosition(10000);
                    await Task.Delay(600);
                    var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(view);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(imagePath))!);
                    using var output = File.Create(imagePath); encoder.Save(output);
                    Console.WriteLine("Rendered control fixture: " + Path.GetFullPath(imagePath));
                }
                Console.WriteLine($"Animated lyric checks passed: {_passed}");
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); result = 1; }
            finally { window?.Close(); app.Shutdown(result); }
        };
        app.Run();
        return result;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        _passed++;
        Console.WriteLine("PASS: " + message);
    }

    private static bool ScrollIsMoving(AnimatedLyricsView view) =>
        (bool)typeof(AnimatedLyricsView).GetField("_scrollMoving", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static async Task NextRenderFrameAsync(AnimatedLyricsView view)
    {
        var previous = (TimeSpan)typeof(AnimatedLyricsView).GetField("_lastRenderingTime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, EventArgs args)
        {
            if (((RenderingEventArgs)args).RenderingTime > previous) completion.TrySetResult();
        }
        CompositionTarget.Rendering += OnRendering;
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    private sealed class ControlledScrollTime : TimeProvider
    {
        private long _timestamp, _resumedAt;
        private bool _running;
        public override long TimestampFrequency => Stopwatch.Frequency;
        public override long GetTimestamp() => _timestamp + (_running ? Stopwatch.GetTimestamp() - _resumedAt : 0);
        public void Advance(TimeSpan elapsed)
        {
            if (_running) throw new InvalidOperationException("The controlled scroll clock is already running in real time.");
            _timestamp += (long)Math.Round(elapsed.TotalSeconds * Stopwatch.Frequency);
        }
        public void Resume() { _resumedAt = Stopwatch.GetTimestamp(); _running = true; }
    }

    private static void CheckRetainedKaraoke()
    {
        var line = new LyricsLine(TimeSpan.FromSeconds(1), "你好世界 á 😀")
        {
            Words = [new LyricsWord(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "你好世界", 0),
                new LyricsWord(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "á", 5),
                new LyricsWord(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), "😀", 8)]
        };
        var text = new OutlinedLyricsText { FontSize = 36, Foreground = Brushes.White, TextAlignment = TextAlignment.Left };
        text.ApplyLyricsStyle(new LyricAppearanceSettings { SungColor = "#10CD77" });
        text.SetLine(line, line.Text);
        text.Measure(new Size(185, 200));
        text.Arrange(new Rect(0, 0, 185, 200));
        text.UpdatePlayback(900);
        var before = DrawPixels(text, 185, 200);
        var layouts = text.LayoutBuildCount;
        var renders = text.RenderCount;
        for (var index = 0; index < 120; index++) text.UpdatePlayback(1000 + index * 12);
        var during = DrawPixels(text, 185, 200);
        Check(!before.SequenceEqual(during), "Retained word clipping updates visible karaoke pixels across wrapped text");
        Check(text.LayoutBuildCount == layouts && text.RenderCount == renders && text.KaraokeLayoutBuildCount == 1,
            "Word frames reuse text shaping and drawing instructions instead of rebuilding text or glyph layouts");
        text.UpdatePlayback(4900);
        var last = DrawPixels(text, 185, 200);
        Check(!last.SequenceEqual(during), "Combining characters and emoji keep their timed highlights");
        text.UpdatePlayback(900);
        Check(DrawPixels(text, 185, 200).SequenceEqual(before), "Seeking backwards clears retained sung clipping without stale highlighted characters");
    }

    private static byte[] DrawPixels(FrameworkElement element, int width, int height)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static async Task<Window> MeasurePlaybackAsync()
    {
        var view = new AnimatedLyricsView();
        var window = new Window { Content = view, Width = 760, Height = 800, ShowActivated = false, ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(12, 16, 21)), Title = "Embistic lyric rendering measurement" };
        view.ApplyAppearance(new LyricAppearanceSettings { ScrollDurationMs = 700, TransitionDurationMs = 320 });
        var text = "风经过安静的夏天把故事留在你我之间";
        var lines = Enumerable.Range(0, 240).Select(index => new LyricsLine(TimeSpan.FromMilliseconds(index * 900), text + "\nA quiet summer carries our stories through the melody.")
        {
            Words = text.Select((character, word) => new LyricsWord(TimeSpan.FromMilliseconds(index * 900 + word * 45),
                TimeSpan.FromMilliseconds(index * 900 + (word + 1) * 45), character.ToString(), word)).ToArray()
        }).ToArray();
        window.Show();
        view.SetDocument(new LyricsDocument { IsSynced = true, Lines = lines });
        var playback = Stopwatch.StartNew();
        var observations = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        observations.Tick += (_, _) => view.UpdatePlayback(27000 + playback.ElapsedMilliseconds, true);
        observations.Start();
        view.UpdatePlayback(27000, true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(2500);
        var scrollChanges = 0;
        var layoutPasses = 0;
        var frames = new List<double>();
        var previous = TimeSpan.Zero;
        var scroller = (ScrollViewer)view.FindName("Scroller");
        ScrollChangedEventHandler onScroll = (_, e) => { if (e.VerticalChange != 0) scrollChanges++; };
        EventHandler onLayout = (_, _) => layoutPasses++;
        EventHandler onFrame = (_, args) =>
        {
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (time == previous) return;
            if (previous != TimeSpan.Zero) frames.Add((time - previous).TotalMilliseconds);
            previous = time;
        };
        scroller.ScrollChanged += onScroll;
        scroller.LayoutUpdated += onLayout;
        CompositionTarget.Rendering += onFrame;
        var texts = ((StackPanel)view.FindName("RowsPanel")).Children.Cast<Button>().Select(button => (OutlinedLyricsText)((StackPanel)button.Content).Children[0]).ToArray();
        var layoutBuilds = texts.Sum(text => text.LayoutBuildCount);
        var renders = texts.Sum(text => text.RenderCount);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        await Task.Delay(6500);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        observations.Stop();
        view.UpdatePlayback(27000 + playback.ElapsedMilliseconds, false);
        scroller.ScrollChanged -= onScroll;
        scroller.LayoutUpdated -= onLayout;
        CompositionTarget.Rendering -= onFrame;
        frames.Sort();
        Console.WriteLine($"LYRIC PERF: frames={frames.Count}, median={frames[frames.Count / 2]:F2}ms, p95={frames[(int)(frames.Count * .95)]:F2}ms, max={frames[^1]:F2}ms, nativeScrollChanges={scrollChanges}, layoutUpdates={layoutPasses}, textLayouts={texts.Sum(text => text.LayoutBuildCount) - layoutBuilds}, textRenders={texts.Sum(text => text.RenderCount) - renders}, UIAllocated={allocated / 1024d:F0}KiB");
        return window;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("FAIL: " + failure);
            await Task.Delay(20);
        }
    }
}
