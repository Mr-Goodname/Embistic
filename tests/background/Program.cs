using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Embistic;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _checks, _saved;
    private static readonly string TemporaryRoot = Path.Combine(Path.GetTempPath(), "Embistic-background-" + Guid.NewGuid().ToString("N"));

    [STAThread]
    private static int Main(string[] args)
    {
        var before = SettingsFingerprint(); var foreground = GetForegroundWindow();
        if (args.Length == 0) { RunChild("--close-direct"); RunChild("--close-unavailable"); }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MainWindow? window = null;
        try
        {
            LoadProductionResources(app);
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            if (args.Length == 0) TestNativeTray();
            window = CreateFixture();
            var tray = Get<TrayService>(window, "_tray");
            Check(tray.Available, "The real Windows Shell accepts the fixture notification icon");
            Check(Get<object?>(window, "_player") is null && Get<object?>(window, "_emby") is null && !Get<bool>(window, "_ready"),
                "Background fixture never starts audio, an Emby connection, or automatic updates");
            if (args.Contains("--close-direct"))
            {
                Get<AppSettings>(window, "_settings").CloseToTray = false;
                window.Close(); PumpUntil(() => Get<bool>(window, "_allowClose") && !window.IsVisible);
                Check(Get<bool>(window, "_closing") && !window.IsVisible && !tray.Available, "Choosing exit on close shuts down the window and removes the native icon");
                Check(_saved == 1 && !Get<DispatcherTimer>(window, "_timer").IsEnabled, "Direct exit persists through the injected sink and stops playback polling");
            }
            else if (args.Contains("--close-unavailable"))
            {
                typeof(TrayService).GetField("<Available>k__BackingField", Private)!.SetValue(tray, false);
                window.Close(); PumpUntil(() => Get<bool>(window, "_allowClose") && !window.IsVisible);
                Check(Get<bool>(window, "_closing") && !window.IsVisible, "Unavailable Shell notification area falls back to exit instead of hiding an inaccessible window");
                Check(_saved == 1, "Tray-unavailable exit persists through the injected sink");
            }
            else
            {
                TestMainWindow(window, tray);
            }
            Check(SettingsFingerprint() == before, "Background and exit checks leave the user's saved settings bytes unchanged");
            Console.WriteLine($"PASS: {_checks} background, native tray and close-choice checks.");
            Console.WriteLine("CHECK_COUNT: " + _checks);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is TargetInvocationException { InnerException: { } inner } ? inner : exception);
            return 1;
        }
        finally
        {
            if (window is not null && !Get<bool>(window, "_allowClose"))
            {
                Set(window, "_closing", true); Set(window, "_allowClose", true);
                foreach (var name in new[] { "_tray", "_spectrumService", "_theme", "_downloads", "_dlna" }) (Get<object?>(window, name) as IDisposable)?.Dispose();
                window.Close();
            }
            app.Shutdown();
            if (foreground != IntPtr.Zero) SetForegroundWindow(foreground);
            var resolved = Path.GetFullPath(TemporaryRoot);
            if (resolved.StartsWith(Path.Combine(Path.GetTempPath(), "Embistic-background-"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static MainWindow CreateFixture()
    {
        var window = new MainWindow();
        window.Loaded -= (RoutedEventHandler)Method(window, "Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), window);
        foreach (var name in new[] { "_timer", "_searchTimer", "_volumeTimer" }) Get<DispatcherTimer>(window, name).Stop();
        Set(window, "_saveSettings", new Action<AppSettings>(_ => _saved++));
        Set(window, "_history", null);
        Get<DownloadService?>(window, "_downloads")?.Dispose(); Set(window, "_downloads", null);
        Set(window, "_settings", new AppSettings
        {
            CheckUpdatesAutomatically = false, DesktopLyricsEnabled = false, CloseToTray = true,
            DownloadDirectory = Path.Combine(TemporaryRoot, "initial"),
            Theme = new ThemeSettings { FollowSystemAccent = false }, Spectrum = new SpectrumSettings { Enabled = false }
        });
        window.ShowInTaskbar = false; window.ShowActivated = false; window.Opacity = 0;
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000; window.Top = -10000;
        window.Show(); Pump(30);
        return window;
    }

    private static void TestMainWindow(MainWindow window, TrayService tray)
    {
        var track = new Track { Id = "background-fixture", Title = "后台演示", Artist = "fixture", DurationMs = 180000 };
        typeof(MainWindow).GetProperty(nameof(MainWindow.Current))!.SetValue(window, track);
        window.Queue.Add(track); Set(window, "_queueIndex", 0); Set(window, "_position", 12345L); Set(window, "_duration", 180000L); Set(window, "_playing", true);
        var timer = Get<DispatcherTimer>(window, "_timer"); timer.Interval = TimeSpan.FromHours(1); timer.Start();
        using var cancellation = new CancellationTokenSource(); Set(window, "_downloadCts", cancellation);
        window.Close(); Pump(20);
        Check(!window.IsVisible && !Get<bool>(window, "_closing") && tray.Available, "Close-to-tray hides the main window without beginning shutdown");
        Check(Get<bool>(window, "_playing") && timer.IsEnabled && Get<long>(window, "_position") == 12345 && window.Current == track && window.Queue.Single() == track,
            "Hiding preserves playback state, position, queue and polling timer");
        Check(!cancellation.IsCancellationRequested && _saved == 0, "Hiding preserves active downloads and does not save or stop the session");
        window.WindowState = WindowState.Minimized;
        TrayService.RequestExistingWindow(new WindowInteropHelper(window).Handle); Pump(35);
        Check(window.IsVisible && window.WindowState == WindowState.Normal && Get<bool>(window, "_playing"), "The real single-instance restore message restores a hidden minimized window and preserves playback");

        Set(window, "_pendingAutomaticCache", track);
        var selectedRoot = Path.Combine(TemporaryRoot, "selected");
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var editor = Application.Current.Windows.OfType<BackgroundSettingsWindow>().Single();
            Get<TextBox>(editor, "_directory").Text = selectedRoot;
            Get<CheckBox>(editor, "_tray").IsChecked = true;
            Get<CheckBox>(editor, "_cache").IsChecked = false;
            FindButton(editor, "保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        Invoke(window, "BackgroundSettings_Click", window, new RoutedEventArgs());
        PumpUntil(() => Get<AppSettings>(window, "_settings").DownloadDirectory == selectedRoot);
        Check(cancellation.IsCancellationRequested && Get<object?>(window, "_pendingAutomaticCache") is null, "Applying background settings cancels the current download and clears queued automatic caching");
        Check(Get<DownloadService>(window, "_downloads").DirectoryPath.StartsWith(selectedRoot + Path.DirectorySeparatorChar) && _saved == 1,
            "Background settings commit the directory and preferences through the injected settings sink");
        Check(Get<bool>(window, "_playing") && window.Current == track, "Updating download settings preserves the current playback session");
        Set(window, "_downloadCts", null);
        using var shutdownCancellation = new CancellationTokenSource(); Set(window, "_downloadCts", shutdownCancellation);
        window.Close(); Pump(15);
        Check(!window.IsVisible && !Get<bool>(window, "_closing"), "Repeated close-to-tray still hides after a settings update");
        var menu = Get<ContextMenu>(tray, "_menu"); Item(menu, "退出 Embistic").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        PumpUntil(() => Get<bool>(window, "_allowClose") && !window.IsVisible);
        Check(Get<bool>(window, "_exitRequested") && Get<bool>(window, "_closing") && !tray.Available,
            "Tray exit overrides close-to-tray and completes shutdown with icon removal");
        Check(shutdownCancellation.IsCancellationRequested && !timer.IsEnabled && _saved == 2,
            "Explicit tray exit cancels downloads, stops polling and saves once");
    }

    private static void TestNativeTray()
    {
        var host = new Window { Width = 100, Height = 100, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        host.Show(); var hwnd = new WindowInteropHelper(host).Handle;
        using var tray = new TrayService(host);
        try
        {
            Check(tray.Available, "Native notification icon registration succeeds with an application icon or fallback");
            var restores = 0; var plays = 0; var previous = 0; var next = 0; var exits = 0;
            tray.RestoreRequested += () => restores++; tray.TogglePlayRequested += () => plays++;
            tray.PreviousRequested += () => previous++; tray.NextRequested += () => next++; tray.ExitRequested += () => exits++;
            tray.Update(new string('歌', 160), true);
            var menu = Get<ContextMenu>(tray, "_menu"); menu.Opacity = 0;
            Check(Get<MenuItem>(tray, "_play").Header?.ToString() == "暂停", "Tray transport label follows the actual playing flag");
            var data = Get<object>(tray, "_data");
            Check(((string)data.GetType().GetField("Tip")!.GetValue(data)!).Length == 127, "Long track names fit the native Shell tooltip buffer");
            SendMessage(hwnd, 0x8000 + 42, IntPtr.Zero, new IntPtr(0x202));
            Check(restores == 1, "Native left-click callback requests restoration");
            SendMessage(hwnd, 0x8000 + 42, IntPtr.Zero, new IntPtr(0x205)); Pump(20);
            Check(menu.IsOpen, "Native right-click callback opens the real WPF transport menu");
            foreach (var name in new[] { "暂停", "上一首", "下一首", "退出 Embistic", "打开 Embistic" }) Item(menu, name).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(plays == 1 && previous == 1 && next == 1 && exits == 1 && restores == 2, "Tray menu routes every transport, restore and exit event exactly once");
            menu.IsOpen = false;
            TrayService.RequestExistingWindow(hwnd); Pump(25);
            Check(restores == 3, "Single-instance sender and receiver exchange the real registered restore message");
            typeof(TrayService).GetMethod("ShellNotifyIcon", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { (object)2u, data });
            var created = Get<uint>(tray, "_taskbarCreated"); SendMessage(hwnd, unchecked((int)created), IntPtr.Zero, IntPtr.Zero);
            Check(tray.Available, "Taskbar recreation re-registers the fixture icon after Shell removes it");
            tray.Dispose(); tray.Dispose();
            TrayService.RequestExistingWindow(hwnd); Pump(15);
            Check(!tray.Available && restores == 3 && !menu.IsOpen, "Disposal removes the icon, closes its menu and detaches the native message hook");
        }
        finally { host.Close(); }
    }

    private static void RunChild(string mode)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Environment.CurrentDirectory };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add(mode);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start close-choice fixture.");
        if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Close-choice fixture exceeded 10 seconds."); }
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
        Console.Write(output);
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        var count = output.Split('\n').Single(line => line.StartsWith("CHECK_COUNT: ", StringComparison.Ordinal));
        _checks += int.Parse(count[13..].Trim());
    }

    private static void LoadProductionResources(Application app)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Embistic", "App.xaml"))) directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Embistic source root not found.");
        var source = XElement.Load(Path.Combine(directory.FullName, "src", "Embistic", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(presentation + "ResourceDictionary",
            source.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Select(attribute => new XAttribute(attribute.Name,
                attribute.Value.StartsWith("clr-namespace:", StringComparison.Ordinal) ? attribute.Value + ";assembly=Embistic" : attribute.Value)),
            source.Element(presentation + "Application.Resources")!.Elements());
        foreach (var element in resources.Descendants().Where(element => element.Name.NamespaceName.StartsWith("clr-namespace:", StringComparison.Ordinal)))
            element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Embistic");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
    private static string SettingsFingerprint() => File.Exists(SettingsService.SettingsPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SettingsService.SettingsPath))) : "missing";
    private static T Get<T>(object target, string name) => (T)(target.GetType().GetField(name, Private)!.GetValue(target))!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static MethodInfo Method(object target, string name) => target.GetType().GetMethod(name, Private)!;
    private static object? Invoke(object target, string name, params object?[] values) => Method(target, name).Invoke(target, values);
    private static MenuItem Item(ContextMenu menu, string text) => menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == text);
    private static Button FindButton(DependencyObject root, string text)
    {
        if (root is Button button && button.Content?.ToString() == text) return button;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        { try { return FindButton(System.Windows.Media.VisualTreeHelper.GetChild(root, i), text); } catch (InvalidOperationException) { } }
        throw new InvalidOperationException("Button not found: " + text);
    }
    private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException("FAIL: " + text); _checks++; Console.WriteLine("PASS: " + text); }
    private static void PumpUntil(Func<bool> completed)
    { var deadline = Stopwatch.StartNew(); while (!completed() && deadline.ElapsedMilliseconds < 5000) Pump(15); Check(completed(), "Asynchronous window operation completes within five seconds"); }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
