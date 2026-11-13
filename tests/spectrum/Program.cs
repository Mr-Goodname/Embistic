using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Embistic.Controls;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;
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
        Check(settings.BarCount == 192 && settings.FrameRate == 60 && settings.Mode == "Mirror", "Shape/count/frame-rate normalization");
        Check(settings.Gain == 1.8 && settings.Height == 90 && settings.Smoothing == .95, "Non-finite response values normalize safely");
        Check(settings.MaxFrequency > settings.MinFrequency && settings.ColorStart == "#ADC9F0", "Invalid frequency range and color recover");
        var copy = settings.Clone(); copy.Height = 70;
        Check(settings.Height == 90, "Preview preference clone remains independent");
        var bins = new float[1025]; bins[16] = .5f;
        var bands = new float[64];
        SpectrumBandMath.Map(bins, 48000, new SpectrumSettings(), bands);
        Check(bands.Max() > .5 && bands.All(value => value is >= 0 and <= 1), "Log bands respond to actual FFT magnitude within normalized bounds");
    }

    private static void TestView()
    {
        var view = new SpectrumView();
        var host = new Window { Content = view, Width = 720, Height = 200, Left = -9000, Top = -9000, ShowActivated = false, ShowInTaskbar = false };
        host.Show(); Pump(80);
        var bins = new float[1025]; bins[16] = .5f;
        view.IsPlaying = true; view.SetSpectrum(bins); Pump(120);
        Check(view.DisplayedBands.Max() > .35 && view.RenderedFrameCount > 0, "Native view renders captured FFT frames");
        foreach (var mode in new[] { "Bars", "Mirror", "Wave", "Line" })
        {
            view.ApplySettings(new SpectrumSettings { Mode = mode, GlowEnabled = mode == "Wave", PeakHold = true });
            view.SetSpectrum(bins); Pump(80);
            var bitmap = new RenderTargetBitmap((int)view.ActualWidth, (int)view.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            Check(bitmap.PixelWidth > 0, $"Native {mode} drawing and effect render");
        }
        view.IsPlaying = false;
        var stoppedFrames = view.RenderedFrameCount; Pump(140);
        Check(view.DisplayedBands.All(value => value == 0) && view.RenderedFrameCount == stoppedFrames, "Paused view clears data and stops frame timer");
        var requested = 0; view.SettingsRequested += () => requested++;
        ((MenuItem)view.ContextMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(requested == 1, "Spectrum context menu requests real settings window");
        host.Close();
    }

    private static void TestEditor()
    {
        var initial = new SpectrumSettings();
        var editor = new SpectrumSettingsWindow(initial) { Left = -9000, Top = -9000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        editor.Show(); Pump(100);
        var events = 0; editor.PreviewChanged += _ => events++;
        var gain = Find<Slider>(editor, value => AutomationProperties.GetAutomationId(value) == "SpectrumGain");
        gain.Value = 3.1; Pump(80);
        Check(Math.Abs(editor.Result.Gain - 3.1) < .01 && events > 0, "Response slider applies independent live preview");
        Check(initial.Gain == 1.8, "Settings editor never mutates caller before save");
        var color = Find<TextBox>(editor, value => AutomationProperties.GetAutomationId(value) == "SpectrumColorStart");
        color.Text = "#"; Pump(40);
        Check(!((Button)editor.FindName("SaveButton")).IsEnabled, "Partial HEX input stays in editor and blocks invalid save");
        color.Text = "#CC22AADD"; Pump(80);
        Check(editor.Result.ColorStart == "#CC22AADD" && ((Button)editor.FindName("SaveButton")).IsEnabled, "ARGB HEX accepted with valid live preview");
        var output = Path.GetFullPath("artifacts/spectrum-checks");
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(editor); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(output, "settings.png"))) encoder.Save(file);
        editor.Close(); var afterClose = events; Pump(100);
        Check(events == afterClose, "Closed editor stops synthetic-preview callbacks");
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
