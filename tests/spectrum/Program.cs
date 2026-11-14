using System.Buffers.Binary;
using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Embistic.Controls;
using Embistic.Models;
using Embistic.Services;
using NAudio.Wave;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main()
    {
        try
        {
            TestFft();
            TestSettings();
            TestCurvesAndMotion();
            MeasureCurveWorkload();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources["Bg"] = new SolidColorBrush(Color.FromRgb(12, 16, 21));
            app.Resources["Panel"] = new SolidColorBrush(Color.FromRgb(19, 25, 32));
            app.Resources["Raised"] = new SolidColorBrush(Color.FromRgb(27, 36, 48));
            app.Resources["Line"] = new SolidColorBrush(Color.FromRgb(39, 49, 62));
            app.Resources["Accent"] = new SolidColorBrush(Color.FromRgb(173, 201, 240));
            app.Resources["Text"] = Brushes.White;
            app.Resources["Muted"] = Brushes.LightSlateGray;
            app.Resources["AccentText"] = Brushes.Black;
            TestView();
            TestMultiViewCadence();
            TestEditor();
            app.Shutdown();
            Console.WriteLine($"PASS: {_checks} real PCM, FFT, native render and spectrum editor checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++; Console.WriteLine("PASS: " + name);
    }

    private static void TestFft()
    {
        foreach (var bits in new[] { 8, 16, 24, 32 })
        {
            var format = new WaveFormat(48000, bits, 2);
            var analyzer = new AudioSpectrumAnalyzer(format);
            var bytes = MakePcm(format, 375, .5, 4096);
            var spectra = new List<float[]>();
            for (var offset = 0; offset < bytes.Length;)
            {
                var take = Math.Min((offset % 251) + 1, bytes.Length - offset);
                analyzer.ProcessPcm(bytes.AsSpan(offset, take), spectra.Add);
                offset += take;
            }
            Check(spectra.Count == 3, $"{bits}-bit fragmented stereo frames preserve FFT overlap");
            var result = spectra[^1];
            var peak = Array.IndexOf(result, result.Max());
            Check(peak == 16 && result[peak] is > .45f and < .55f, $"{bits}-bit PCM tone frequency and amplitude");
        }
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var floating = new AudioSpectrumAnalyzer(floatFormat);
        var floatResults = new List<float[]>();
        floating.ProcessPcm(MakePcm(floatFormat, 1500, .2, 2048), floatResults.Add);
        Check(Array.IndexOf(floatResults[0], floatResults[0].Max()) == 64, "IEEE float FFT frequency matches real samples");
        var silenceResults = new List<float[]>();
        new AudioSpectrumAnalyzer(floatFormat).ProcessPcm(new byte[2048 * floatFormat.BlockAlign], silenceResults.Add);
        Check(silenceResults[0].All(value => value == 0), "Silent PCM generates zero spectrum without invented response");
        var empty = new List<float[]>();
        new AudioSpectrumAnalyzer(floatFormat).ProcessPcm(new byte[floatFormat.BlockAlign * 100], empty.Add);
        Check(empty.Count == 0, "Incomplete FFT window never publishes fabricated bins");
    }

    private static byte[] MakePcm(WaveFormat format, double frequency, double amplitude, int samples)
    {
        var bytes = new byte[samples * format.BlockAlign];
        for (var i = 0; i < samples; i++)
        {
            var value = Math.Sin(2 * Math.PI * frequency * i / format.SampleRate) * amplitude;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var output = bytes.AsSpan(i * format.BlockAlign + channel * format.BitsPerSample / 8, format.BitsPerSample / 8);
                if (format.Encoding == WaveFormatEncoding.IeeeFloat) BinaryPrimitives.WriteInt32LittleEndian(output, BitConverter.SingleToInt32Bits((float)value));
                else if (format.BitsPerSample == 8) output[0] = (byte)Math.Clamp(Math.Round(value * 128 + 128), 0, 255);
                else if (format.BitsPerSample == 16) BinaryPrimitives.WriteInt16LittleEndian(output, (short)Math.Round(value * 32767));
                else if (format.BitsPerSample == 24)
                {
                    var integer = (int)Math.Round(value * 8388607);
                    output[0] = (byte)integer; output[1] = (byte)(integer >> 8); output[2] = (byte)(integer >> 16);
                }
                else BinaryPrimitives.WriteInt32LittleEndian(output, (int)Math.Round(value * 2147483647));
            }
        }
        return bytes;
    }

    private static void TestSettings()
    {
        var settings = new SpectrumSettings { BarCount = int.MaxValue, Gain = double.NaN, Height = double.PositiveInfinity, MinFrequency = 3500, MaxFrequency = 100, ColorStart = "invalid", Mode = "mirror", Smoothing = 4, FrameRate = 100 };
        settings.Normalize();
        Check(settings.BarCount == 192 && settings.FrameRate == 100 && settings.Mode == "Mirror", "Shape/count/high-refresh frame-rate normalization");
        Check(settings.Gain == 1.8 && settings.Height == 90 && settings.Smoothing == .95, "Non-finite response values normalize safely");
        Check(settings.MaxFrequency > settings.MinFrequency && settings.ColorStart == "#ADC9F0", "Invalid frequency range and color recover");
        var copy = settings.Clone(); copy.Height = 70;
        Check(settings.Height == 90, "Preview preference clone remains independent");
        var legacy = new SpectrumSettings { FrameRate = 30 }; legacy.Normalize();
        Check(legacy.FrameRate == 60 && legacy.Revision == 1, "Older 30-fps default upgrades once to 60 fps");
        legacy.FrameRate = 30; legacy.Normalize();
        Check(legacy.FrameRate == 30, "An explicit 30-fps preference survives later normalization");
        var positioned = new SpectrumSettings { ShowTop = true, ShowBottom = false, ShowLeft = true, ShowRight = true, ShowRing = true };
        positioned.Normalize(); var positionedCopy = positioned.Clone(); positionedCopy.ShowRing = false;
        Check(positioned.ShowTop && !positioned.ShowBottom && positioned.ShowLeft && positioned.ShowRight && positioned.ShowRing, "Multiple edge positions and album ring persist independently");
        Check(!positionedCopy.ShowRing && positioned.ShowRing, "Position preference clone remains independent");
        var missingPosition = new SpectrumSettings { ShowBottom = false }; missingPosition.Normalize();
        Check(missingPosition.ShowBottom, "Invalid persisted empty placement recovers to bottom");
        var bins = new float[1025]; bins[16] = .5f;
        var bands = new float[64];
        SpectrumBandMath.Map(bins, 48000, new SpectrumSettings(), bands);
        Check(bands.Max() > .5 && bands.All(value => value is >= 0 and <= 1), "Log bands respond to actual FFT magnitude within normalized bounds");
        var mappedBefore = SpectrumBandMath.SharedMappingCount;
        var shared = new float[64]; var spectrum = new SpectrumSettings();
        for (var placement = 0; placement < 5; placement++) SpectrumBandMath.MapShared(bins, 48000, spectrum, shared);
        Check(SpectrumBandMath.SharedMappingCount - mappedBefore == 1 && shared.SequenceEqual(bands),
            "Five placements share one FFT mapping without changing measured amplitudes");
        spectrum.Gain = .25;
        SpectrumBandMath.MapShared(bins, 48000, spectrum, shared);
        Check(SpectrumBandMath.SharedMappingCount - mappedBefore == 2 && shared.Max() < bands.Max(),
            "Shared mapping respects live changes to the frequency response");
    }

    private static void TestCurvesAndMotion()
    {
        var bands = new float[] { 0, .1f, .9f, .05f, .4f, 1, .2f, .3f, 0, .1f, .8f, .1f, .6f, 0, .1f, 0 };
        var geometry = SpectrumCurveMath.Cartesian(bands, 640, 100, false);
        var path = PathGeometry.CreateFromGeometry(geometry);
        var points = BezierPoints(path.Figures[0]);
        Check(points.Count == (bands.Length + 1) * 3 && !path.Figures[0].IsClosed,
            "Frequency line uses cubic Beziers for every span, rather than a polygon");
        var bounded = true; var tangentContinuous = true; var start = path.Figures[0].StartPoint;
        for (var i = 0; i < points.Count; i += 3)
        {
            var end = points[i + 2];
            var minimum = Math.Min(start.Y, end.Y); var maximum = Math.Max(start.Y, end.Y);
            for (var sample = 0; sample <= 20; sample++)
            {
                var point = Cubic(start, points[i], points[i + 1], end, sample / 20.0);
                bounded &= point.Y >= minimum - .00001 && point.Y <= maximum + .00001 && point.X is >= 0 and <= 640;
            }
            if (i + 3 < points.Count)
                tangentContinuous &= SameDirection(end - points[i + 1], points[i + 3] - end);
            start = end;
        }
        Check(bounded, "Cubic line stays between actual sample amplitudes without overshoot");
        Check(tangentContinuous, "Every line peak and trough has a continuous tangent");
        var isolated = new float[64]; isolated[32] = 1;
        var roundedPeak = PathGeometry.CreateFromGeometry(SpectrumCurveMath.Cartesian(isolated, 640, 100, false));
        var roundedPoints = BezierPoints(roundedPeak.Figures[0]);
        Check(roundedPoints.Where((_, i) => i % 3 == 2).Count(point => point.Y < 99) >= 5,
            "An isolated FFT spike spreads into a rounded hill independently of response smoothing");
        var silentCurve = PathGeometry.CreateFromGeometry(SpectrumCurveMath.Cartesian(new float[64], 640, 100, false));
        Check(BezierPoints(silentCurve.Figures[0]).All(point => point.Y == 100), "Spatial curve smoothing never invents a response for silent data");
        var wave = PathGeometry.CreateFromGeometry(SpectrumCurveMath.Cartesian(bands, 640, 100, true));
        Check(wave.Figures[0].IsClosed && wave.Figures[0].IsFilled, "Wave fills beneath the same smooth cubic contour");

        var ringBands = Enumerable.Range(0, 64).Select(i => (float)(.5 + .5 * Math.Cos(i * Math.PI * 2 / 64 * 5))).ToArray();
        var center = new Point(100, 100);
        var ring = SpectrumCurveMath.Radial(ringBands, center, 70, 96, true);
        var ringPath = PathGeometry.CreateFromGeometry(ring); var ringPoints = BezierPoints(ringPath.Figures[0]);
        Check(ringPath.Figures[0].IsClosed && ringPoints.Count == ringBands.Length * 3, "Album ring uses a closed cubic contour from all real FFT bands");
        Check(!ring.FillContains(center) && ring.FillContains(new Point(100, 16)), "Filled radial wave leaves the album center unobscured");
        var ringBounded = true; start = ringPath.Figures[0].StartPoint;
        for (var i = 0; i < ringPoints.Count; i += 3)
        {
            for (var sample = 0; sample <= 20; sample++)
            {
                var point = Cubic(start, ringPoints[i], ringPoints[i + 1], ringPoints[i + 2], sample / 20.0);
                var radius = (point - center).Length;
                ringBounded &= radius >= 69.95 && radius <= 96.05;
            }
            start = ringPoints[i + 2];
        }
        Check(ringBounded, "Radial cubic interpolation stays within its ring bounds");
        Check(SameDirection(ringPath.Figures[0].StartPoint - ringPoints[^2], ringPoints[0] - ringPath.Figures[0].StartPoint),
            "Closed radial curve has a smooth seam");

        TestCoverContours();

        var fastBlend = SpectrumMotionMath.Blend(1.0 / 60, 0);
        Check(fastBlend is > 0 and < 1, "Zero response smoothing still interpolates between FFT packets");
        foreach (var smoothing in new[] { 0.0, .65, .95 })
        {
            var whole = SpectrumMotionMath.Blend(1.0 / 30, smoothing);
            var half = SpectrumMotionMath.Blend(1.0 / 60, smoothing);
            Check(Math.Abs(whole - (1 - (1 - half) * (1 - half))) < .000001,
                $"Response timing remains equal at 30 and 60 fps for smoothing {smoothing}");
        }
    }

    private static void TestCoverContours()
    {
        var buffer = new SpectrumCurveBuffer();
        var amplitudes = Enumerable.Repeat(.7f, 192).ToArray();
        var center = new Point(275, 275);
        var circle = buffer.Radial(amplitudes, center, 223, 271, true);
        Check(!circle.FillContains(new Point(275, 61)) && circle.FillContains(new Point(275, 40)),
            "Vinyl ring begins beyond the full 430px record and leaves its grooves uncovered");
        var rounded = buffer.Radial(amplitudes, center, 223, 271, true, "RoundedSquare", 40);
        var cover = new RectangleGeometry(new Rect(60, 60, 430, 430), 32, 32);
        var uncovered = true;
        for (var y = 60; y <= 490; y += 5)
            for (var x = 60; x <= 490; x += 5)
                if (cover.FillContains(new Point(x, y))) uncovered &= !rounded.FillContains(new Point(x, y));
        Check(uncovered && rounded.FillContains(new Point(275, 35)) && rounded.FillContains(new Point(35, 275)),
            "Rounded-square spectrum follows all four sides without filling over the cover");
        var outline = PathGeometry.CreateFromGeometry(rounded).Figures[0]; var points = BezierPoints(outline);
        Check(SameDirection(outline.StartPoint - points[^2], points[0] - outline.StartPoint),
            "Rounded-square spectrum has a continuous closed seam");
        var maxNormalError = 0d;
        for (var i = 0; i < buffer.Contour.Count; i++)
        {
            var sample = buffer.Contour[i];
            maxNormalError = Math.Max(maxNormalError, Math.Abs(Vector.Multiply(sample.Normal, sample.Tangent)) + Math.Abs(sample.Normal.Length - 1));
        }
        Check(maxNormalError < .000001, "Rounded-cover bars extend perpendicular to the cover outline");
    }

    private static List<Point> BezierPoints(PathFigure figure)
    {
        var points = new List<Point>();
        foreach (var segment in figure.Segments)
        {
            if (segment is BezierSegment bezier) { points.Add(bezier.Point1); points.Add(bezier.Point2); points.Add(bezier.Point3); }
            else if (segment is PolyBezierSegment poly) points.AddRange(poly.Points);
            else throw new Exception("Curve unexpectedly contains " + segment.GetType().Name);
        }
        return points;
    }

    private static void MeasureCurveWorkload()
    {
        var bands = Enumerable.Range(0, 192).Select(i => (float)(.5 + .4 * Math.Sin(i * .31))).ToArray();
        var timings = new double[360];
        var curves = Enumerable.Range(0, 5).Select(_ => new SpectrumCurveBuffer()).ToArray();
        for (var warmup = 0; warmup < 20; warmup++)
        {
            for (var edge = 0; edge < 4; edge++) curves[edge].Cartesian(bands, 1200, 140, false);
            curves[4].Radial(bands, new Point(275, 275), 223, 271, false);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < timings.Length; frame++)
        {
            var started = Stopwatch.GetTimestamp();
            for (var edge = 0; edge < 4; edge++) curves[edge].Cartesian(bands, 1200, 140, false);
            curves[4].Radial(bands, new Point(275, 275), 223, 271, false);
            timings[frame] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Array.Sort(timings);
        Console.WriteLine($"PERF five 192-band curves: median={timings[timings.Length / 2]:F3}ms p95={timings[(int)(timings.Length * .95)]:F3}ms allocation={bytes / timings.Length:N0} bytes/frame");
        Check(bytes / timings.Length < 125000, "Five maximum-band curve views reuse their convolution, contour and interpolation buffers");
    }

    private static bool SameDirection(Vector left, Vector right)
    {
        left.Normalize(); right.Normalize(); return (left - right).Length < .000001;
    }

    private static Point Cubic(Point p0, Point p1, Point p2, Point p3, double t)
    {
        var u = 1 - t;
        return new Point(u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
            u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
    }

    private static void TestView()
    {
        var view = new SpectrumView();
        var host = new Window { Content = view, Width = 720, Height = 200, Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        host.Show(); Pump(80);
        var bins = new float[1025]; bins[16] = .5f;
        view.IsPlaying = true; view.SetSpectrum(bins); Pump(120);
        Check(view.DisplayedBands.Max() > .35 && view.RenderedFrameCount > 0, "Native view renders captured FFT frames");
        var wakeDispatches = view.WakeDispatchCount;
        Task.Run(() => { for (var packet = 0; packet < 1000; packet++) view.SetSpectrum(bins); }).GetAwaiter().GetResult();
        Pump(40);
        Check(view.WakeDispatchCount == wakeDispatches, "Active composition consumes packet bursts without posting per-packet UI work");
        foreach (var mode in new[] { "Bars", "Mirror", "Wave", "Line" })
        {
            view.ApplySettings(new SpectrumSettings { Mode = mode, GlowEnabled = mode == "Wave", PeakHold = true });
            view.SetSpectrum(bins); Pump(80);
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            Check(bitmap.PixelWidth > 0, $"Native {mode} drawing and effect render");
        }
        view.IsRadial = true; view.Height = 170; Pump(40);
        foreach (var mode in new[] { "Bars", "Mirror", "Wave", "Line" })
        {
            view.ApplySettings(new SpectrumSettings { Mode = mode, Smoothing = 0, FrameRate = 144, PeakHold = true });
            view.SetSpectrum(bins); Pump(70);
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            Check(bitmap.PixelHeight == 170 && view.DisplayedBands.Max() > .35, $"Native radial {mode} draws actual bands and honors container sizing");
        }
        view.RadialShape = "RoundedSquare"; view.RadialInset = 20; view.RadialCornerRadius = 16;
        foreach (var mode in new[] { "Bars", "Mirror", "Wave", "Line" })
        {
            view.ApplySettings(new SpectrumSettings { Mode = mode, Smoothing = 0, FrameRate = 144, PeakHold = true });
            view.SetSpectrum(bins); Pump(40);
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            Check(bitmap.PixelHeight == 170, $"Native rounded-square {mode} honors outward contour sizing");
        }
        view.IsPlaying = false;
        view.IsPlaying = true; view.SetSpectrum(bins);
        var fastFrames = view.RenderedFrameCount; Pump(35);
        var firstResponse = view.DisplayedBands.Max(); Pump(65);
        Check(view.RenderedFrameCount - fastFrames >= 3 && view.DisplayedBands.Max() > firstResponse,
            "Low-smoothing view continues drawing intermediate composition frames between FFT updates");
        view.Visibility = Visibility.Collapsed;
        wakeDispatches = view.WakeDispatchCount;
        Task.Run(() => { for (var packet = 0; packet < 1000; packet++) view.SetSpectrum(bins); }).GetAwaiter().GetResult();
        var hiddenFrames = view.RenderedFrameCount; Pump(90);
        Check(!view.IsRendering && view.RenderedFrameCount == hiddenFrames, "Hidden view unsubscribes from composition without background redraws");
        Check(view.WakeDispatchCount == wakeDispatches, "Hidden placements receive capture data without scheduling UI callbacks");
        view.Visibility = Visibility.Visible; Pump(35);
        Check(view.IsRendering, "Showing a playing spectrum resumes composition frames");
        view.IsPlaying = false;
        var stoppedFrames = view.RenderedFrameCount; Pump(140);
        Check(!view.IsRendering && view.DisplayedBands.All(value => value == 0) && view.RenderedFrameCount == stoppedFrames, "Paused view clears data and unsubscribes from composition");
        var requested = 0; view.SettingsRequested += () => requested++;
        ((MenuItem)view.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(requested == 1, "Spectrum context menu requests real settings window");
        view.IsPlaying = true; view.SetSpectrum(bins); Pump(25); host.Close(); Pump(40);
        Check(!view.IsRendering, "Unloaded view releases the global composition subscription");
        TestSilentWake();
    }

    private static void TestSilentWake()
    {
        var view = new SpectrumView(); view.ApplySettings(new SpectrumSettings { Smoothing = 0 });
        var host = new Window { Content = view, Width = 450, Height = 160, Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        host.Show(); view.IsPlaying = true; view.SetSpectrum(new float[1025]); Pump(650);
        Check(!view.IsRendering, "A silent visible spectrum sleeps after its last captured packet");
        var wakeDispatches = view.WakeDispatchCount; var bins = new float[1025]; bins[16] = .5f;
        Task.Run(() => { for (var packet = 0; packet < 1000; packet++) view.SetSpectrum(bins); }).GetAwaiter().GetResult();
        Check(view.WakeDispatchCount == wakeDispatches + 1, "A packet burst queues only one wake for a sleeping visible spectrum");
        Pump(80);
        Check(view.IsRendering && view.DisplayedBands.Max() > .35, "New audio wakes a sleeping spectrum and resumes real output");
        host.Close();
    }

    private static void TestMultiViewCadence()
    {
        var grid = new Grid();
        var views = Enumerable.Range(0, 5).Select(_ => new SpectrumView()).ToArray();
        foreach (var view in views)
        {
            view.ApplySettings(new SpectrumSettings { Mode = "Line", BarCount = 192, Smoothing = 0, FrameRate = 60 });
            view.IsPlaying = true; grid.Children.Add(view);
        }
        views[0].VerticalAlignment = VerticalAlignment.Top;
        views[1].VerticalAlignment = VerticalAlignment.Bottom;
        views[2].HorizontalAlignment = HorizontalAlignment.Left;
        views[2].LayoutTransform = new RotateTransform(90);
        views[3].HorizontalAlignment = HorizontalAlignment.Right;
        views[3].LayoutTransform = new RotateTransform(-90);
        views[4].IsRadial = true; views[4].Width = views[4].Height = 550;
        views[4].RadialInset = 52; views[4].RadialShape = "RoundedSquare"; views[4].RadialCornerRadius = 40;
        var host = new Window { Content = grid, Width = 1200, Height = 740, Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var pcm = MakePcm(format, 750, .5, 1024);
        var analyzer = new AudioSpectrumAnalyzer(format);
        var feed = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        feed.Tick += (_, _) => analyzer.ProcessPcm(pcm, bins => { foreach (var view in views) view.SetSpectrum(bins); });
        host.Show(); feed.Start(); Pump(240);
        var layouts = 0; var intervals = new List<double>(); var lastAt = 0L; var lastRenderingTime = TimeSpan.MinValue;
        EventHandler layout = (_, _) => layouts++;
        EventHandler rendering = (_, e) =>
        {
            if (e is RenderingEventArgs frame)
            {
                if (frame.RenderingTime == lastRenderingTime) return;
                lastRenderingTime = frame.RenderingTime;
            }
            var now = Stopwatch.GetTimestamp();
            if (lastAt != 0) intervals.Add(Stopwatch.GetElapsedTime(lastAt, now).TotalMilliseconds);
            lastAt = now;
        };
        grid.LayoutUpdated += layout; CompositionTarget.Rendering += rendering;
        var counts = views.Select(view => view.RenderedFrameCount).ToArray();
        Pump(900);
        grid.LayoutUpdated -= layout; CompositionTarget.Rendering -= rendering;
        feed.Stop(); host.Close();
        Check(layouts == 0, "Five active frequency placements redraw without running a WPF layout pass per frame");
        Check(views.Select((view, i) => view.RenderedFrameCount - counts[i]).Min() >= 15,
            "Five maximum-band placements keep animating together on real composition frames");
        intervals.Sort();
        Console.WriteLine($"PERF native five-placement composition: unique-frames={intervals.Count + 1} spectrum-frames={views.Min(view => view.RenderedFrameCount)} interval-median={intervals[intervals.Count / 2]:F2}ms interval-p95={intervals[(int)(intervals.Count * .95)]:F2}ms layout-passes={layouts}");
    }

    private static void TestEditor()
    {
        var initial = new SpectrumSettings();
        var editor = new SpectrumSettingsEditor(initial);
        var host = new Window { Content = editor, Width = 450, Height = 780, Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        host.Show(); Pump(100);
        var events = 0; editor.PreviewChanged += _ => events++;
        var gain = Find<Slider>(editor, value => AutomationProperties.GetAutomationId(value) == "SpectrumGain");
        gain.Value = 3.1; Pump(80);
        Check(Math.Abs(editor.Result.Gain - 3.1) < .01 && events > 0, "Response slider applies independent live preview");
        Check(initial.Gain == 1.8, "Settings editor publishes independent settings snapshots for automatic persistence");
        Check(editor.FindName("SaveButton") is null && editor.FindName("PreviewSpectrum") is null && editor.FindName("PreviewRingSpectrum") is null,
            "Inline spectrum settings have no save button or duplicate synthetic spectrum renderer");
        var bottom = Find<CheckBox>(editor, value => AutomationProperties.GetAutomationId(value) == "SpectrumShowBottom");
        var beforeInvalid = events;
        bottom.IsChecked = false; editor.Flush();
        Check(!editor.IsInputValid && events == beforeInvalid, "An empty placement selection is explained without publishing invalid auto-save data");
        Find<CheckBox>(editor, value => AutomationProperties.GetAutomationId(value) == "SpectrumShowTop").IsChecked = true;
        Find<CheckBox>(editor, value => AutomationProperties.GetAutomationId(value) == "SpectrumShowRing").IsChecked = true;
        Pump(50);
        Check(editor.Result.ShowTop && !editor.Result.ShowBottom && editor.Result.ShowRing && editor.IsInputValid,
            "Multiple placements update the live preview and remain independently selectable");
        Find<Slider>(editor, value => AutomationProperties.GetAutomationId(value) == "SpectrumFrameRate").Value = 120;
        editor.Flush();
        Check(editor.Result.FrameRate == 120, "Editor supports independent 120-fps selection");
        Find<TabControl>(editor, _ => true).SelectedIndex = 1; Pump(30);
        var color = Find<TextBox>(editor, value => AutomationProperties.GetAutomationId(value) == "SpectrumColorStart");
        beforeInvalid = events;
        color.Text = "#"; editor.Flush();
        Check(!editor.IsInputValid && events == beforeInvalid, "Partial HEX input stays editable and never publishes an invalid preference");
        color.Text = "#CC22AADD"; Pump(80);
        Check(editor.Result.ColorStart == "#CC22AADD" && editor.IsInputValid, "ARGB HEX accepted with valid live preview");
        color.Text = "#ABC"; editor.Flush();
        Check(editor.Result.ColorStart == "#AABBCC" && editor.IsInputValid, "Three-digit color input expands consistently for preview and persistence");
        Find<TabControl>(editor, _ => true).SelectedIndex = 0; Pump(30);
        Find<ComboBox>(editor, value => AutomationProperties.GetName(value) == "频谱形态").SelectedValue = "Line";
        Pump(80);
        Check(editor.Result.Mode == "Line" && editor.IsInputValid, "Selecting the smooth line publishes settings for the real immersive spectrum");
        var output = Path.GetFullPath("artifacts/spectrum-checks");
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "settings.png"))) encoder.Save(file);
        gain.Value = 3.3;
        var afterClose = -1;
        editor.Unloaded += (_, _) => afterClose = events;
        host.Close(); Pump(100);
        Check(editor.Result.Gain == 3.3, "Closing the inline editor flushes the latest continuous-slider edit");
        Check(afterClose >= 0 && events == afterClose, "Unloaded inline editor leaves no delayed preview callbacks");
    }

    private static T Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T match && predicate(match)) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            try { return Find(child, predicate); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException(typeof(T).Name + " not found");
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
