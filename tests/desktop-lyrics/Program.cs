using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Embistic.Controls;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;

internal static class Program
{
    private static int _checks, _underlyingClicks;
    private static Point _expectedPointer;
    private static bool _lastCursorMoveSucceeded;
    private static int _lastCursorMoveError;
    private static Func<string>? _diagnostics;
    private static readonly Queue<string> PointerHistory = new();
    private static string _lastPointerState = "";
    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        GetCursorPos(out var originalPointer); var originalForeground = GetForegroundWindow();
        var result = 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            Window? behind = null;
            DesktopLyricsWindow? overlay = null;
            try
            {
                var width = Math.Clamp(SystemParameters.WorkArea.Width - 180, 400, 680);
                behind = new Window { Width = width + 100, Height = 400, Left = SystemParameters.WorkArea.Left + 35,
                    Top = SystemParameters.WorkArea.Top + 35, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false, Topmost = true, Background = new SolidColorBrush(Color.FromRgb(45, 62, 80)),
                    Content = new TextBlock { Text = "Embistic desktop lyric input fixture", Foreground = Brushes.White, Margin = new Thickness(12) } };
                behind.MouseLeftButtonDown += (_, _) => _underlyingClicks++;
                behind.Show();
                var behindHandle = new WindowInteropHelper(behind).Handle;
                SetForegroundWindow(behindHandle);
                var outside = behind.PointToScreen(new Point(10, 10));
                MovePointer(outside);
                overlay = new DesktopLyricsWindow { Left = behind.Left + 40, Top = behind.Top + 70 };
                var style = new LyricAppearanceSettings { DesktopWidth = width, DesktopFontSize = 32, ShowTrackInfo = true,
                    DesktopBackgroundEnabled = true, BackgroundColor = "#00112233", BackgroundOpacity = 0,
                    AnimationsEnabled = false, ShadowEnabled = false };
                overlay.ApplyAppearance(style);
                overlay.SetTrack("桌面歌词测试", "Embistic");
                overlay.UpdateLyrics(LyricsService.ParseLrc("[00:01]风吹过安静的夏天\n[00:01]A quiet summer melody\n[00:09]下一句歌词"), 1500);
                overlay.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(120);
                var overlayHandle = new WindowInteropHelper(overlay).Handle;
                _diagnostics = () => DescribeState(overlay, behind, overlayHandle, behindHandle);
                var surface = (Border)overlay.FindName("Surface");
                var controls = (FrameworkElement)overlay.FindName("HoverControls");
                var current = (OutlinedLyricsText)overlay.FindName("CurrentLine");
                var baseline = current.PointToScreen(new Point());
                var bounds = Bounds(overlayHandle);
                var idle = Render(overlay);
                Check(AlphaAt(idle, 2, 2) == 0 && controls.Visibility != Visibility.Visible, "Idle corners are exactly transparent and controls are hidden even with a legacy background preference");
                Check(overlay.Opacity == 1 && overlay.ResizeMode == ResizeMode.NoResize, "The window uses neither an almost-transparent input sheet nor changing native resize styles");
                var optionChanges = 0; overlay.OptionsChanged += () => optionChanges++;
                Save(idle, "idle.png", args);
                var points = new[] { new Point(2, 2), new Point(width - 2, 2), new Point(2, overlay.ActualHeight - 2),
                    new Point(width - 2, overlay.ActualHeight - 2), new Point(width / 2, 2),
                    new Point(2, overlay.ActualHeight / 2), new Point(width - 2, overlay.ActualHeight / 2),
                    new Point(width / 2, overlay.ActualHeight - 2) };
                for (var index = 0; index < points.Length; index++)
                {
                    MovePointer(outside);
                    await Until(() => controls.Visibility != Visibility.Visible, "Hover controls did not hide after leaving the window");
                    var target = overlay.PointToScreen(points[index]);
                    MovePointer(target);
                    await Until(() => controls.Visibility == Visibility.Visible && WindowFromPoint(ToNative(target)) == overlayHandle,
                        "The transparent rectangle failed native hover/hit testing at point " + index);
                    Check(SameBounds(bounds, Bounds(overlayHandle)) && Distance(baseline, current.PointToScreen(new Point())) < .25,
                        $"Native rectangular hover point {index + 1} shows controls without moving the window or lyric baseline");
                }
                Check(optionChanges == 0, "All rectangular hover enter/leave cycles avoid settings-save events");
                var hovered = Render(overlay);
                Check(AlphaAt(hovered, 4, 4) >= 50, "Hover background remains visible when saved opacity and color alpha are both zero");
                Check(AlphaAt(hovered, 0, hovered.PixelHeight / 2) > 0, "Hover frame is drawn independently of lyric padding");
                Save(hovered, "hover.png", args);
                var blank = overlay.PointToScreen(new Point(8, overlay.ActualHeight / 2));
                MovePointer(blank); await Task.Delay(65); Click(); await Task.Delay(100);
                Check(_underlyingClicks == 0, "Clicking an unlocked blank margin is captured by the lyric window instead of the app below");
                Check(SameBounds(bounds, Bounds(overlayHandle)) && Distance(baseline, current.PointToScreen(new Point())) < .25,
                    "A click without drag does not shift the lyric window or baseline");
                var beforeLockChanges = optionChanges;
                var lockButton = ((Panel)controls).Children.OfType<Button>().Single(button => Equals(button.Content, "锁定"));
                MovePointer(lockButton.PointToScreen(new Point(lockButton.ActualWidth / 2, lockButton.ActualHeight / 2)));
                await Task.Delay(65); Click();
                await Until(() => overlay.IsLocked, "The native toolbar lock click did not reach its button");
                await Until(() => controls.Visibility != Visibility.Visible && WindowFromPoint(ToNative(blank)) == behindHandle,
                    "Lock did not restore native mouse pass-through");
                Check((GetWindowLongPtr(overlayHandle, -20).ToInt64() & 0x20) != 0 && AlphaAt(Render(overlay), 4, 4) == 0,
                    "Locked lyrics are transparent and set WS_EX_TRANSPARENT");
                var ink = FindInk(idle);
                var inkScreen = overlay.PointToScreen(ink);
                MovePointer(inkScreen);
                await Task.Delay(100);
                Check(WindowFromPoint(ToNative(inkScreen)) == behindHandle, "Locked lyric text pixels pass native hit testing through to the window below");
                Click(); await Task.Delay(90);
                Check(_underlyingClicks == 1, "A native click through locked lyric text reaches the underlying window");
                Save(Render(overlay), "locked.png", args);
                overlay.IsLocked = false;
                await Until(() => controls.Visibility == Visibility.Visible && WindowFromPoint(ToNative(inkScreen)) == overlayHandle,
                    "Unlock did not recover rectangle interaction under the stationary pointer");
                Check(SameBounds(bounds, Bounds(overlayHandle)) && Distance(baseline, current.PointToScreen(new Point())) < .25,
                    "Lock and unlock preserve the window rectangle and lyric baseline");
                Check(optionChanges == beforeLockChanges + 2, "Pointer enter/leave does not trigger settings-save events");
                MovePointer(outside);
                await Until(() => controls.Visibility != Visibility.Visible, "Final leave did not hide controls");
                Check(Pixels(Render(overlay)).SequenceEqual(Pixels(idle)), "Leaving restores the exact original lyric-only pixels with no frame or tint residue");
                var grip = (Thumb)overlay.FindName("WidthGrip");
                MovePointer(overlay.PointToScreen(new Point(width - 8, overlay.ActualHeight - 8)));
                await Until(() => grip.Visibility == Visibility.Visible, "Resize handle did not appear");
                var gripStart = grip.PointToScreen(new Point(8, 8));
                MovePointer(gripStart);
                await Until(() => grip.IsMouseOver && WindowFromPoint(ToNative(gripStart)) == overlayHandle, "Resize pointer did not reach the native handle");
                MouseInput(0x0002);
                await Until(() => grip.IsMouseCaptured, "Resize handle did not capture native mouse input");
                var dpi = VisualTreeHelper.GetDpi(overlay).DpiScaleX;
                MovePointer(new Point(gripStart.X - 50 * dpi, gripStart.Y)); await Task.Delay(60); MouseInput(0x0004);
                await Until(() => !grip.IsMouseCaptured, "Resize handle did not release native capture");
                Check(Math.Abs(overlay.Width - (width - 50)) < 2 && overlay.SizeToContent == SizeToContent.Height,
                    "The custom resize handle changes width while lyrics retain automatic wrapped height");
                overlay.Hide();
                MovePointer(blank); await Task.Delay(80);
                Check(WindowFromPoint(ToNative(blank)) == behindHandle, "Hidden lyrics leave no native hit-test surface behind");
                Console.WriteLine($"Desktop lyric checks passed: {_checks}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine("DESKTOP INPUT DIAGNOSTICS: " + _diagnostics?.Invoke());
                foreach (var observation in PointerHistory) Console.Error.WriteLine(observation);
                result = 1;
            }
            finally
            {
                MouseInput(0x0004); Mouse.Capture(null);
                behind?.Hide(); overlay?.Close(); behind?.Close();
                SetCursorPos(originalPointer.X, originalPointer.Y);
                if (originalForeground != IntPtr.Zero) SetForegroundWindow(originalForeground);
                app.Shutdown(result);
            }
        };
        app.Run(); return result;
    }
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException("FAIL: " + message); _checks++; Console.WriteLine("PASS: " + message); }
    private static async Task Until(Func<bool> predicate, string error)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            ObservePointer();
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new InvalidOperationException(error);
            await Task.Delay(12);
        }
    }
    private static double Distance(Point first, Point second) => (first - second).Length;
    private static NativePoint ToNative(Point point) => new() { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) };
    private static void MovePointer(Point point)
    {
        _expectedPointer = point;
        var native = ToNative(point);
        _lastCursorMoveSucceeded = SetCursorPos(native.X, native.Y);
        _lastCursorMoveError = Marshal.GetLastWin32Error();
        ObservePointer();
    }
    private static void ObservePointer()
    {
        GetCursorPos(out var actual); GetPhysicalCursorPos(out var physical);
        var state = $"expected={ToNative(_expectedPointer).X},{ToNative(_expectedPointer).Y} actual={actual.X},{actual.Y} physical={physical.X},{physical.Y} hitExpected={WindowFromPoint(ToNative(_expectedPointer)):X} hitActual={WindowFromPoint(actual):X} foreground={GetForegroundWindow():X}";
        if (_lastPointerState == state) return;
        _lastPointerState = state;
        if (PointerHistory.Count == 20) PointerHistory.Dequeue();
        PointerHistory.Enqueue(DateTimeOffset.Now.ToString("HH:mm:ss.fff") + " " + state);
    }
    private static string DescribeState(DesktopLyricsWindow overlay, Window behind, IntPtr overlayHandle, IntPtr behindHandle)
    {
        GetCursorPos(out var actual); GetPhysicalCursorPos(out var physical);
        var rectangle = Bounds(overlayHandle); var behindRectangle = Bounds(behindHandle);
        var controls = (FrameworkElement)overlay.FindName("HoverControls");
        var hovered = typeof(DesktopLyricsWindow).GetField("_hovered", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(overlay);
        var timer = typeof(DesktopLyricsWindow).GetField("_hoverTimer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(overlay) as DispatcherTimer;
        var context = GetThreadDpiAwarenessContext(); var windowContext = GetWindowDpiAwarenessContext(overlayHandle);
        var wpfDpi = VisualTreeHelper.GetDpi(overlay);
        var zOrder = new List<string>();
        for (var handle = overlayHandle; handle != IntPtr.Zero && zOrder.Count < 7; handle = GetWindow(handle, 3))
        {
            GetWindowThreadProcessId(handle, out var processId);
            zOrder.Add($"{handle:X}(pid={processId},ex={GetWindowLongPtr(handle, -20):X})");
        }
        return $"expected={_expectedPointer}; actual={actual.X},{actual.Y}; physical={physical.X},{physical.Y}; SetCursorPos={_lastCursorMoveSucceeded}/error={_lastCursorMoveError}; " +
            $"overlay={overlayHandle:X} behind={behindHandle:X} hitExpected={WindowFromPoint(ToNative(_expectedPointer)):X} hitActual={WindowFromPoint(actual):X} foreground={GetForegroundWindow():X}; " +
            $"overlayRect={rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}; behindRect={behindRectangle.Left},{behindRectangle.Top},{behindRectangle.Right},{behindRectangle.Bottom}; " +
            $"overlayVisible={overlay.IsVisible} loaded={overlay.IsLoaded} locked={overlay.IsLocked} hovered={hovered} timer={timer?.IsEnabled} controls={controls.Visibility} mouseOver={overlay.IsMouseOver}; " +
            $"overlayDIP={overlay.Left},{overlay.Top},{overlay.ActualWidth},{overlay.ActualHeight}; behindVisible={behind.IsVisible}; dpi={wpfDpi.DpiScaleX},{wpfDpi.DpiScaleY}/{GetDpiForWindow(overlayHandle)}; " +
            $"threadDpiContext={context:X}/{GetAwarenessFromDpiAwarenessContext(context)} windowDpiContext={windowContext:X}/{GetAwarenessFromDpiAwarenessContext(windowContext)}; overlayAndWindowsAbove={string.Join(" -> ", zOrder)}";
    }
    private static NativeRect Bounds(IntPtr window) { if (!GetWindowRect(window, out var bounds)) throw new InvalidOperationException("Cannot read fixture window bounds"); return bounds; }
    private static bool SameBounds(NativeRect a, NativeRect b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    private static RenderTargetBitmap Render(DesktopLyricsWindow overlay)
    {
        overlay.UpdateLayout();
        var root = (FrameworkElement)overlay.Content;
        var dpi = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root); return bitmap;
    }
    private static byte[] Pixels(BitmapSource bitmap) { var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels; }
    private static byte AlphaAt(BitmapSource bitmap, int x, int y) => Pixels(bitmap)[(y * bitmap.PixelWidth + x) * 4 + 3];
    private static Point FindInk(BitmapSource bitmap)
    {
        var pixels = Pixels(bitmap);
        for (var y = 30; y < bitmap.PixelHeight - 30; y++)
            for (var x = 30; x < bitmap.PixelWidth - 30; x++)
                if (pixels[(y * bitmap.PixelWidth + x) * 4 + 3] >= 230) return new Point(x * 96 / bitmap.DpiX, y * 96 / bitmap.DpiY);
        throw new InvalidOperationException("Fixture has no opaque lyric pixels");
    }
    private static void Save(BitmapSource bitmap, string name, string[] args)
    {
        var folder = args.FirstOrDefault() ?? Path.Combine("artifacts", "desktop-lyrics");
        Directory.CreateDirectory(folder);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name)); encoder.Save(stream);
    }
    private static void Click() => SendInput(2, [new Input { Type = 0, Mouse = new MouseInputData { Flags = 0x0002 } }, new Input { Type = 0, Mouse = new MouseInputData { Flags = 0x0004 } }], Marshal.SizeOf<Input>());
    private static void MouseInput(uint flags) => SendInput(1, [new Input { Type = 0, Mouse = new MouseInputData { Flags = flags } }], Marshal.SizeOf<Input>());
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInputData Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInputData { public int X, Y; public uint Data, Flags, Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr handle);
    [DllImport("user32.dll")] private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr handle, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
}
