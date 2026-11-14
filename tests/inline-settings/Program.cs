using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        _root = FindRoot();
        var fingerprint = SettingsFingerprint();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        LoadResources(app);
        try
        {
            TestEditor();
            TestOwnerSessions();
            Check(fingerprint == SettingsFingerprint(), "Inline settings checks never write the user's saved preferences");
            Console.WriteLine($"PASS: {_checks} inline appearance, source editing and automatic persistence checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex); return 1; }
        finally { app.Shutdown(); }
    }

    private static void TestEditor()
    {
        var initial = new LyricAppearanceSettings { AnimationsEnabled = false };
        var editor = new LyricAppearanceEditor(initial);
        var sources = new LyricsSourcesEditor(new[] { Source("First"), Source("Second") }, () => null);
        editor.SetSourcesEditor(sources);
        var host = new Window { Content = editor, Width = 384, Height = 780, WindowStyle = WindowStyle.None,
            Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        var changes = 0; var sourceChanges = 0;
        editor.AppearanceChanged += _ => changes++;
        sources.SourcesChanged += _ => sourceChanges++;
        try
        {
            host.Show(); Pump(150);
            Check(changes == 0 && sourceChanges == 0, "Opening settings does not publish unchanged preferences");
            var slider = Named<Slider>(editor, "MainSizeSlider");
            var data = slider.DataContext;
            for (var step = 0; step < 12; step++) { slider.Value = 26 + step * .5; Pump(5); }
            WaitUntil(() => editor.Result.MainFontSize == 31.5, 1000);
            Check(editor.Result.MainFontSize == 31.5 && initial.MainFontSize == 26,
                "Continuous slider input previews the exact fractional value using an independent snapshot");
            Check(ReferenceEquals(slider, Named<Slider>(editor, "MainSizeSlider")) && ReferenceEquals(data, slider.DataContext),
                "Continuous preview preserves the original slider and its data context");
            Check(changes > 0 && changes < 12, "Continuous slider notifications are coalesced without rebuilding the editor");
            Capture(editor, "fonts-384.png");
            var tabs = Named<TabControl>(editor, "SettingsTabs"); tabs.SelectedIndex = 1; Pump(60);
            var color = Find<TextBox>(editor, x => AutomationProperties.GetName(x) == "当前歌词颜色 HEX");
            var beforeInvalid = changes; color.Text = "#"; editor.Flush();
            Check(!editor.IsInputValid && changes == beforeInvalid, "Incomplete color input does not overwrite valid appearance preferences");
            color.Text = "#ABC"; editor.Flush();
            Check(editor.IsInputValid && editor.Result.CurrentColor == "#AABBCC", "Completing a color immediately publishes the normalized appearance");
            for (var index = 0; index < 5; index++)
            {
                tabs.SelectedIndex = index; Pump(30);
                var scroll = Find<ScrollViewer>(editor, _ => true);
                Check(scroll.ScrollableWidth < .1 && scroll.ActualWidth <= 384,
                    $"Appearance tab {index + 1} remains usable without horizontal scrolling at 384px");
            }
            editor.SelectSources(); Pump(450);
            var appearanceBeforeSources = changes;
            var name = Find<TextBox>(sources, x => AutomationProperties.GetAutomationId(x) == "LyricsSourceName");
            name.Text = "Renamed"; name.CaretIndex = 3; sources.Flush(); Pump(50);
            Check(sourceChanges == 1 && sources.Result[0].Name == "Renamed", "Editing a source publishes its valid configuration automatically");
            Check(ReferenceEquals(name, Find<TextBox>(sources, x => AutomationProperties.GetAutomationId(x) == "LyricsSourceName")) && name.CaretIndex == 3,
                "Saving a source retains the focused text field and caret position");
            Check(changes == appearanceBeforeSources, "Source editing never triggers unrelated lyric appearance redraws");
            Pump(850);
            Check(sourceChanges == 1, "Refreshing a source label does not start a repeating auto-save loop");
            var enabled = Find<CheckBox>(sources, x => AutomationProperties.GetAutomationId(x) == "LyricsSourceEnabled");
            enabled.IsChecked = false; sources.Flush();
            Check(!sources.Result[0].Enabled && sourceChanges == 2, "Source enable switches participate in automatic persistence");
            ClickContent(sources, "下移"); sources.Flush();
            Check(sources.Result[0].Name == "Second" && sources.Result[1].Name == "Renamed", "Reordering sources persists their lookup priority");
            var exported = sources.ExportConfiguration();
            var imported = new LyricsSourcesEditor(Array.Empty<NetworkLyricsSource>(), () => null);
            var importedEvents = 0; imported.SourcesChanged += _ => importedEvents++;
            imported.ImportConfiguration(exported);
            Check(importedEvents == 1 && imported.Result.Count == 2 && !imported.Result[1].Enabled,
                "Import and export preserve order, enabled state and automatic commit behavior");
            var beforeImport = JsonSerializer.Serialize(imported.Result);
            var rejected = false;
            try { imported.ImportConfiguration("[{\"Name\":\"broken\",\"Enabled\":true,\"UrlTemplate\":\"file:///local\"}]"); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected && JsonSerializer.Serialize(imported.Result) == beforeImport, "Invalid imported sources leave the current configuration intact");
            var url = Find<TextBox>(sources, x => AutomationProperties.GetAutomationId(x) == "LyricsSourceUrlTemplate");
            enabled = Find<CheckBox>(sources, x => AutomationProperties.GetAutomationId(x) == "LyricsSourceEnabled");
            enabled.IsChecked = true; url.Text = "not a URL"; sources.Flush();
            var exportRejected = false;
            try { sources.ExportConfiguration(); } catch (ArgumentException) { exportRejected = true; }
            Check(!sources.IsInputValid && exportRejected, "Incomplete enabled source addresses are neither persisted nor exported");
            url.Text = "https://example.invalid/lyrics?title={title}"; sources.Flush();
            Capture(editor, "sources-384.png");
            editor.SelectAppearance(); Pump(30);
            slider.Value = 29;
            var afterUnload = -1; editor.Unloaded += (_, _) => afterUnload = changes;
            host.Close(); Pump(120);
            Check(editor.Result.MainFontSize == 29 && afterUnload >= 0 && changes == afterUnload,
                "Unloading flushes the final slider change and leaves no delayed appearance callback");
        }
        finally { host.Close(); }
    }

    private static void TestOwnerSessions()
    {
        var window = new MainWindow();
        var settings = new AppSettings { CheckUpdatesAutomatically = false, CloseToTray = false,
            Appearance = new LyricAppearanceSettings { AnimationsEnabled = false } };
        var savedSize = -1d; var saves = 0;
        try
        {
            window.Loaded -= (RoutedEventHandler)Method(window, "Window_Loaded").CreateDelegate(typeof(RoutedEventHandler), window);
            window.SourceInitialized -= (EventHandler)Method(window, "Window_SourceInitialized").CreateDelegate(typeof(EventHandler), window);
            foreach (var name in new[] { "_timer", "_searchTimer", "_volumeTimer" }) Field<DispatcherTimer>(window, name).Stop();
            Set(window, "_saveSettings", new Action<AppSettings>(s => { savedSize = s.Appearance.MainFontSize; saves++; }));
            Set(window, "_settings", settings); Set(window, "_history", null);
            (Field<object?>(window, "_downloads") as IDisposable)?.Dispose(); Set(window, "_downloads", null);
            window.ShowInTaskbar = false; window.ShowActivated = false; window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -9000; window.Top = -9000; window.Width = 1360; window.Height = 850;
            window.Show(); Invoke(window, "ShowPage", "lyricSettings"); Pump(180);
            var editor = Field<LyricAppearanceEditor>(window, "_lyricEditor");
            Named<Slider>(editor, "MainSizeSlider").Value = 31.5;
            WaitUntil(() => savedSize == 31.5, 1500);
            Check(savedSize == 31.5 && settings.Appearance.MainFontSize == 31.5,
                "The real owner previews and persists valid edits without a save button");
            var beforeSwitch = saves;
            Named<Slider>(editor, "MainSizeSlider").Value = 33;
            Invoke(window, "OpenSpectrumSettings");
            Check(savedSize == 33 && saves > beforeSwitch, "Switching setting panels flushes the outgoing final edit");
            Pump(60);
            var spectrum = Field<SpectrumSettingsEditor>(window, "_spectrumEditor");
            var gain = Find<Slider>(spectrum, x => AutomationProperties.GetAutomationId(x) == "SpectrumGain");
            gain.Value = 2.7; spectrum.Flush();
            Invoke(window, "OpenLyricsSettings", false);
            Check(settings.Spectrum.Gain == 2.7, "Opening appearance after spectrum retains the final spectrum setting");
            Pump(60);
            var currentEditor = Field<LyricAppearanceEditor>(window, "_lyricEditor");
            Named<Slider>(currentEditor, "MainSizeSlider").Value = 35; currentEditor.Flush();
            Named<Slider>(editor, "MainSizeSlider").Value = 18; editor.Flush();
            gain.Value = 6; spectrum.Flush();
            Pump(80);
            Check(settings.Appearance.MainFontSize == 35 && settings.Spectrum.Gain == 2.7,
                "Late callbacks from retired editors cannot overwrite the newly opened settings session");
            Invoke(window, "ShowPage", "lyrics"); Invoke(window, "OpenLyricsSettings", false); Pump(80);
            var immersive = Named<ImmersivePlayerView>(window, "ImmersivePlayer");
            var closing = Field<LyricAppearanceEditor>(window, "_lyricEditor");
            Named<Slider>(closing, "MainSizeSlider").Value = 36; immersive.HideSettingsPanel();
            Check(savedSize == 36, "Closing the left immersive panel synchronously commits its last edit");
            Named<Slider>(closing, "MainSizeSlider").Value = 19; closing.Flush(); Pump(80);
            Check(settings.Appearance.MainFontSize == 36, "Closed immersive panels no longer publish stale appearance changes");
        }
        finally
        {
            Set(window, "_closing", true); Set(window, "_allowClose", true);
            foreach (var name in new[] { "_timer", "_searchTimer", "_volumeTimer", "_inlineSaveTimer" }) Field<DispatcherTimer>(window, name).Stop();
            foreach (var field in new[] { "_spectrumService", "_theme", "_downloads", "_dlna", "_lyrics" }) (Field<object?>(window, field) as IDisposable)?.Dispose();
            window.Close();
        }
    }

    private static NetworkLyricsSource Source(string name) => new() { Name = name, UrlTemplate = "https://example.invalid/lyrics?title={title}" };
    private static T Named<T>(FrameworkElement root, string name) where T : class => (T)root.FindName(name);
    private static T Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T value && predicate(value)) return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            try { return Find(VisualTreeHelper.GetChild(root, i), predicate); } catch (InvalidOperationException) { }
        throw new InvalidOperationException(typeof(T).Name + " not found");
    }
    private static void ClickContent(DependencyObject root, string label) => Find<Button>(root, b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static MethodInfo Method(object obj, string name) => obj.GetType().GetMethod(name, Private)!;
    private static object? Invoke(object obj, string name, params object[] args) => Method(obj, name).Invoke(obj, args);
    private static T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, Private)!.GetValue(obj)!;
    private static void Set(object obj, string name, object? value) => obj.GetType().GetField(name, Private)!.SetValue(obj, value);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; Console.WriteLine("PASS: " + message); }
    private static void WaitUntil(Func<bool> condition, int timeout)
    {
        var elapsed = Stopwatch.StartNew(); while (!condition() && elapsed.ElapsedMilliseconds < timeout) Pump(20);
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(FrameworkElement element, string name)
    {
        var folder = Path.Combine(_root, "artifacts", "inline-settings"); Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, name)); encoder.Save(output);
    }
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "Embistic", "Embistic.csproj"))) return directory.FullName;
        throw new InvalidOperationException("Project root not found");
    }
    private static string SettingsFingerprint() => File.Exists(SettingsService.SettingsPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(SettingsService.SettingsPath))) : "missing";
    private static void LoadResources(Application app)
    {
        var appXaml = XElement.Load(Path.Combine(_root, "src", "Embistic", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:Embistic;assembly=Embistic"),
            new XAttribute(XNamespace.Xmlns + "controls", "clr-namespace:Embistic.Controls;assembly=Embistic"),
            appXaml.Element(presentation + "Application.Resources")!.Elements());
        foreach (var element in resources.Descendants().Where(e => e.Name.NamespaceName is "clr-namespace:Embistic" or "clr-namespace:Embistic.Controls"))
            element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Embistic");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
}
