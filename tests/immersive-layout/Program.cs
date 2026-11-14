using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Embistic.Controls;
using Embistic.Models;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = FindRoot();
        LoadResources(app, root);
        var view = new ImmersivePlayerView();
        var measuredHost = new MeasuredHost { Child = view };
        var window = new Window
        {
            Content = measuredHost, Width = Math.Min(1280, SystemParameters.WorkArea.Width - 48),
            Height = Math.Min(820, SystemParameters.WorkArea.Height - 48), ShowActivated = false,
            ShowInTaskbar = false, Topmost = true, Opacity = 1, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Left = SystemParameters.WorkArea.Left + 24, Top = SystemParameters.WorkArea.Top + 24,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            view.ApplyAppearance(new LyricAppearanceSettings { AnimationsEnabled = true });
            view.ApplySpectrum(new SpectrumSettings { Enabled = true, ShowRing = true, ShowBottom = true, ShowTop = true });
            // This checks real composition frames. A fully off-screen HWND can be
            // throttled past the entire 220 ms animation, even with a pumped dispatcher.
            var contentRendered = false; var firstComposition = false;
            window.ContentRendered += (_, _) => contentRendered = true;
            EventHandler firstFrame = (_, _) => firstComposition = true;
            CompositionTarget.Rendering += firstFrame;
            try
            {
                window.Show();
                WaitFor(() => contentRendered && firstComposition, "The visible fixture did not render its first composition frame");
            }
            finally { CompositionTarget.Rendering -= firstFrame; }
            var art = WriteArtwork(root);
            view.SetTrack(new Track { Id = "cover-layout-fixture", Title = "夜航", Artist = "Embistic", ImageUrl = new Uri(art).AbsoluteUri });
            view.UpdatePlayback(0, true); Pump(180);
            var ring = Find<SpectrumView>(view, "SpectrumRing");
            var disc = Find<Grid>(view, "DiscRotor");
            Check(view.CoverPresentation == "Vinyl" && disc.IsVisible, "The existing disc remains the default cover presentation");
            Check(Math.Abs(ring.ActualWidth - 550) < .1 && ring.RadialShape == "Circle", "Disc ring receives a larger independent square canvas");
            var baselineRadius = ring.ActualWidth * .5 - ring.RadialInset;
            Check(baselineRadius > disc.ActualWidth * .5 + 5, "The disc spectrum starts outside the entire vinyl, with an explicit gap");
            Check(Find<Canvas>(view, "NeedleCanvas").IsVisible, "The record arm remains visible with the ring enabled");
            var backdrop = Find<Image>(view, "BackdropImage");
            Check(backdrop.Source is BitmapSource { PixelWidth: <= 96, IsFrozen: true } && backdrop.Effect is null,
                "Backdrop is a frozen preblurred thumbnail, without a full-window effect surface");
            foreach (var mode in new[] { "Circle", "RoundedSquare", "Vinyl" })
            {
                view.CoverPresentation = mode; Pump();
                Check(Find<Grid>(view, "CircleCover").IsVisible == (mode == "Circle")
                    && Find<Grid>(view, "RoundedCover").IsVisible == (mode == "RoundedSquare")
                    && disc.IsVisible == (mode == "Vinyl"), $"{mode} displays exactly its selected cover");
                Check(ring.RadialShape == (mode == "RoundedSquare" ? "RoundedSquare" : "Circle"), $"{mode} uses its matching spectrum outline");
                var rotating = (bool)typeof(ImmersivePlayerView).GetField("_rotating", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
                Check(rotating == (mode == "Vinyl" && SystemParameters.ClientAreaAnimation), $"Only the selected vinyl mode rotates");
            }
            view.CoverPresentation = "RoundedSquare";
            Check(Math.Abs(ring.RadialCornerRadius - 40) < .1, "Square ring corners include the cover corner radius and outside gap");
            var display = new DisplaySettings { CoverPresentation = "RoundedSquare" };
            Check(display.Clone().CoverPresentation == "RoundedSquare", "Cover choice survives the persisted display-settings copy");
            display.CoverPresentation = "unknown"; display.Normalize();
            Check(display.CoverPresentation == "Vinyl", "Old or invalid cover preferences fall back to vinyl");

            var lyricsWidth = view.LyricsView.ActualWidth;
            var lyricsLeft = view.LyricsView.TranslatePoint(new Point(), view).X;
            var panelBody = new TextBlock { Text = "实时样式预览", FontSize = 24 };
            var closed = 0; view.SettingsPanelClosed += () => closed++;
            view.ShowSettingsPanel(panelBody); Pump();
            var panel = Find<Border>(view, "SettingsPanelSurface");
            Check(view.IsSettingsPanelOpen && panel.IsVisible && view.HasOpenMenu, "Settings open inside the left cover column and keep the controls usable");
            Check(Math.Abs(view.LyricsView.ActualWidth - lyricsWidth) < .1 && Math.Abs(view.LyricsView.TranslatePoint(new Point(), view).X - lyricsLeft) < .1,
                "Opening settings preserves the right lyric viewport width and position");
            Check(panel.TranslatePoint(new Point(panel.ActualWidth, 0), view).X < lyricsLeft,
                "The embedded editor never overlays the right lyrics");
            Check(!Find<Viewbox>(view, "CoverViewbox").IsVisible && !ring.IsVisible, "Hidden artwork and its ring stop rendering while editing");
            view.HideSettingsPanel(); Pump();
            Check(!view.IsSettingsPanelOpen && closed == 1 && Find<ContentControl>(view, "SettingsPanelContent").Content is null,
                "Closing settings detaches the editor and notifies automatic-save owners once");

            view.SetPlayerChrome(106, true, false); Pump();
            var spectrum = view.Spectrum;
            Check(Math.Abs(BottomBaseline(view) - (view.ActualHeight - 106)) < .1, "Visible player places the spectrum immediately above its top edge");
            var measuredBefore = measuredHost.Measures; var arrangedBefore = measuredHost.Arranges;
            var retractStarted = false; var retractFinished = false;
            var intermediate = double.NaN; var retractFrames = 0;
            // Start on a composition boundary and retain an actual intermediate
            // frame. A later dispatcher poll must not erase a frame already observed.
            EventHandler observeRetraction = (_, _) =>
            {
                if (!retractStarted)
                {
                    retractStarted = true;
                    view.SetPlayerChrome(106, false, true);
                    return;
                }
                retractFrames++;
                var baseline = BottomBaseline(view);
                if (double.IsNaN(intermediate) && baseline > view.ActualHeight - 106 && baseline < view.ActualHeight) intermediate = baseline;
                retractFinished |= Math.Abs(baseline - view.ActualHeight) < .1;
            };
            CompositionTarget.Rendering += observeRetraction;
            try
            {
                WaitFor(() => retractStarted, "The compositor did not start the retract animation");
                if (SystemParameters.ClientAreaAnimation)
                    WaitFor(() => double.IsFinite(intermediate) || retractFinished, "The compositor did not deliver an intermediate retract frame");
                Check(!SystemParameters.ClientAreaAnimation || (intermediate > view.ActualHeight - 106 && intermediate < view.ActualHeight),
                    "Spectrum descends through real intermediate positions while controls retract");
                WaitFor(() => retractFinished, "The lower spectrum did not finish retracting");
            }
            finally { CompositionTarget.Rendering -= observeRetraction; }
            Console.WriteLine($"RETRACT COMPOSITION: frames={retractFrames}, intermediate={intermediate:0.###}, start={view.ActualHeight - 106:0.###}, end={view.ActualHeight:0.###}");
            Check(Math.Abs(BottomBaseline(view) - view.ActualHeight) < .1, "Retracted player lets the spectrum reach the window boundary");
            Check(measuredHost.Measures == measuredBefore && measuredHost.Arranges == arrangedBefore,
                "Spectrum displacement causes zero extra immersive measure/arrange passes");
            // Use the same entry as a real pointer interaction so the idle-hide
            // deadline is renewed while measuring the visible upper controls.
            Invoke(view, "ShowOptions");
            var options = Find<Grid>(view, "TopOptions");
            var topSpectrum = Find<SpectrumView>(view, "SpectrumTop");
            WaitFor(() => topSpectrum.TranslatePoint(new Point(0, topSpectrum.ActualHeight), view).Y >= options.TranslatePoint(new Point(0, options.ActualHeight), view).Y + 9,
                "The upper spectrum did not clear the visible controls");
            var topBaseline = topSpectrum.TranslatePoint(new Point(0, topSpectrum.ActualHeight), view).Y;
            Check(topBaseline >= options.TranslatePoint(new Point(0, options.ActualHeight), view).Y + 9,
                "Visible upper spectrum starts below the complete upper options row");
            Invoke(view, "SetOptionsVisible", false);
            WaitFor(() => Math.Abs(topSpectrum.TranslatePoint(new Point(0, topSpectrum.ActualHeight), view).Y) < .1,
                "The upper spectrum did not finish retracting");
            Check(Math.Abs(topSpectrum.TranslatePoint(new Point(0, topSpectrum.ActualHeight), view).Y) < .1,
                "Hidden upper controls let the upper spectrum return to the top boundary");
            if (args.Contains("--screenshots"))
            {
                Invoke(view, "ShowOptions"); view.SetPlayerChrome(106, true, false);
                view.LyricsView.SetDocument(new LyricsDocument { IsSynced = true, Lines = new[] { new LyricsLine(TimeSpan.Zero, "城市渐渐安静"), new LyricsLine(TimeSpan.FromSeconds(5), "让旋律陪我回家") } });
                view.LyricsView.UpdatePosition(0); view.SetSpectrumPlaying(true);
                foreach (var mode in new[] { "Circle", "RoundedSquare", "Vinyl" })
                {
                    view.CoverPresentation = mode; view.SetSpectrum(Enumerable.Repeat(.12f, 1025).ToArray(), 48000); Pump(200);
                    SaveScreenshot(view, Path.Combine(root, "artifacts", "immersive-layout-" + mode + ".png"));
                }
            }
            Console.WriteLine($"Completed: {_checks} immersive layout checks passed; control motion measure/arrange delta: 0/0.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { view.SetSpectrumPlaying(false); view.UpdatePlayback(0, false); window.Close(); app.Shutdown(); }
    }

    private static double BottomBaseline(ImmersivePlayerView view) => view.Spectrum.TranslatePoint(new Point(0, view.Spectrum.ActualHeight), view).Y;
    private static T Find<T>(FrameworkElement root, string name) where T : class => (T)(root.FindName(name) ?? throw new InvalidOperationException(name));
    private static void Invoke(object obj, string method, params object[] args) => typeof(ImmersivePlayerView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
    private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); _checks++; Console.WriteLine("PASS " + label); }
    private static void WaitFor(Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.ElapsedMilliseconds < 1400) Pump(10);
        if (!condition()) throw new InvalidOperationException(failure);
    }
    private static void Pump(int milliseconds = 35)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "Embistic", "App.xaml"))) return directory.FullName;
        throw new DirectoryNotFoundException("Embistic project root");
    }
    private static void LoadResources(Application app, string root)
    {
        var appXaml = XElement.Load(Path.Combine(root, "src", "Embistic", "App.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:Embistic;assembly=Embistic"),
            new XAttribute(XNamespace.Xmlns + "controls", "clr-namespace:Embistic.Controls;assembly=Embistic"), appXaml.Element(presentation + "Application.Resources")!.Elements());
        foreach (var element in resources.Descendants().Where(element => element.Name.NamespaceName is "clr-namespace:Embistic" or "clr-namespace:Embistic.Controls"))
            element.Name = XName.Get(element.Name.LocalName, element.Name.NamespaceName + ";assembly=Embistic");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
    private static string WriteArtwork(string root)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(65, 39, 117), Color.FromRgb(239, 142, 124), 35), null, new Rect(0, 0, 512, 512));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 251, 198, 146)), null, new Point(305, 190), 132, 132);
        }
        var image = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var path = Path.Combine(root, "artifacts", "immersive-layout-cover.png"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream); return path;
    }
    private static void SaveScreenshot(FrameworkElement view, string path)
    {
        var image = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class MeasuredHost : Decorator
    {
        public int Measures { get; private set; }
        public int Arranges { get; private set; }
        protected override Size MeasureOverride(Size constraint) { Measures++; return base.MeasureOverride(constraint); }
        protected override Size ArrangeOverride(Size arrangeBounds) { Arranges++; return base.ArrangeOverride(arrangeBounds); }
    }
}
