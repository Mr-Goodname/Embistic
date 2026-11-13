using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Embistic.Models;

namespace Embistic.Controls;

/// <summary>Native drawing of real FFT data, coalesced at the selected frame rate.</summary>
public sealed class SpectrumView : FrameworkElement
{
    private SpectrumSettings _settings = new();
    private readonly DispatcherTimer _timer;
    private float[]? _pending;
    private float[]? _latest;
    private float[] _bands = new float[64], _target = new float[64], _peaks = new float[64];
    private int _sampleRate = 48000, _wakeQueued;
    private long _dataAt, _frameAt;
    private LinearGradientBrush _brush = new();
    public event Action? SettingsRequested;
    public int RenderedFrameCount { get; private set; }
    internal IReadOnlyList<float> DisplayedBands => _bands;

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(SpectrumView), new PropertyMetadata(false, OnPlaybackChanged));
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

    public SpectrumView()
    {
        Height = 90;
        Focusable = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher) { Interval = TimeSpan.FromSeconds(1.0 / 30) };
        _timer.Tick += (_, _) => DrawFrame();
        Loaded += (_, _) => Wake();
        Unloaded += (_, _) => { _timer.Stop(); Interlocked.Exchange(ref _pending, null); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Wake(); else _timer.Stop(); };
        SizeChanged += (_, _) => { RebuildBrush(); InvalidateVisual(); };
        var menu = new ContextMenu();
        var configure = new MenuItem { Header = "频谱样式设置" };
        configure.Click += (_, _) => SettingsRequested?.Invoke();
        menu.Items.Add(configure);
        ContextMenu = menu;
        ApplySettings(_settings);
    }

    public void SetSpectrum(float[] bins, int sampleRate = 48000)
    {
        if (bins is null || bins.Length < 2 || sampleRate < 8000) return;
        Volatile.Write(ref _sampleRate, sampleRate);
        Interlocked.Exchange(ref _pending, bins);
        Interlocked.Exchange(ref _dataAt, Stopwatch.GetTimestamp());
        if (Interlocked.Exchange(ref _wakeQueued, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { Interlocked.Exchange(ref _wakeQueued, 0); Wake(); });
    }

    public void ApplySettings(SpectrumSettings settings)
    {
        _settings = settings.Clone(); _settings.Normalize();
        Height = _settings.Height;
        _timer.Interval = TimeSpan.FromSeconds(1.0 / _settings.FrameRate);
        if (_bands.Length != _settings.BarCount)
        {
            _bands = new float[_settings.BarCount]; _target = new float[_settings.BarCount]; _peaks = new float[_settings.BarCount];
        }
        Effect = _settings.GlowEnabled ? new DropShadowEffect
        {
            Color = Parse(_settings.ColorEnd), BlurRadius = _settings.GlowRadius, ShadowDepth = 0, Opacity = .5
        } : null;
        RebuildBrush();
        if (!_settings.Enabled) { _timer.Stop(); Array.Clear(_bands); Array.Clear(_peaks); }
        else Wake();
        InvalidateVisual();
    }

    private static void OnPlaybackChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        var view = (SpectrumView)obj;
        if ((bool)args.NewValue) view.Wake();
        else
        {
            view._timer.Stop(); view._latest = null; Interlocked.Exchange(ref view._pending, null);
            Array.Clear(view._bands); Array.Clear(view._peaks); view.InvalidateVisual();
        }
    }

    private void Wake()
    {
        if (!IsLoaded || !IsVisible || !IsPlaying || !_settings.Enabled) return;
        if (!_timer.IsEnabled) { _frameAt = Stopwatch.GetTimestamp(); _timer.Start(); }
    }

    private void DrawFrame()
    {
        if (!IsLoaded || !IsVisible || !IsPlaying || !_settings.Enabled) { _timer.Stop(); return; }
        var frame = Interlocked.Exchange(ref _pending, null);
        if (frame is not null) _latest = frame;
        var now = Stopwatch.GetTimestamp();
        var delta = Math.Clamp((now - _frameAt) / (double)Stopwatch.Frequency, .001, .2);
        _frameAt = now;
        var stale = (now - Volatile.Read(ref _dataAt)) / (double)Stopwatch.Frequency > .45;
        if (_latest is not null && !stale)
            SpectrumBandMath.Map(_latest, Volatile.Read(ref _sampleRate), _settings, _target);
        else Array.Clear(_target);
        var smoothing = (float)Math.Pow(_settings.Smoothing, delta * 30);
        var active = false;
        for (var i = 0; i < _bands.Length; i++)
        {
            _bands[i] = _bands[i] * smoothing + _target[i] * (1 - smoothing);
            if (_bands[i] < .0005f) _bands[i] = 0;
            _peaks[i] = Math.Max(_bands[i], _peaks[i] - (float)(delta * .38));
            active |= _bands[i] > 0 || (_settings.PeakHold && _peaks[i] > 0);
        }
        RenderedFrameCount++;
        InvalidateVisual();
        if (stale && !active) _timer.Stop();
    }

    private void RebuildBrush()
    {
        _brush = new LinearGradientBrush(Parse(_settings.ColorStart), Parse(_settings.ColorEnd), 0)
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, ActualHeight),
            EndPoint = new Point(Math.Max(1, ActualWidth), 0)
        };
        _brush.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!_settings.Enabled || ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushOpacity(_settings.Opacity);
        var width = ActualWidth / _bands.Length;
        var height = ActualHeight - 3;
        if (_settings.Mode is "Wave" or "Line")
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new Point(0, height), _settings.Mode == "Wave", _settings.Mode == "Wave");
                for (var i = 0; i < _bands.Length; i++)
                    path.LineTo(new Point((i + .5) * width, height - _bands[i] * height), true, false);
                path.LineTo(new Point(ActualWidth, height), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(_settings.Mode == "Wave" ? _brush : null, new Pen(_brush, _settings.Mode == "Line" ? 2 : .8), geometry);
        }
        else
        {
            var mirror = _settings.Mode == "Mirror";
            for (var i = 0; i < _bands.Length; i++)
            {
                var barHeight = _bands[i] * (mirror ? height * .5 : height);
                if (barHeight <= .2) continue;
                var barWidth = width * _settings.BarWidthRatio;
                var x = i * width + (width - barWidth) * .5;
                var y = mirror ? height * .5 - barHeight : height - barHeight;
                var bounds = new Rect(x, y, barWidth, mirror ? barHeight * 2 : barHeight);
                var radius = _settings.RoundedBars ? Math.Min(barWidth * .5, 3) : 0;
                dc.DrawRoundedRectangle(_brush, null, bounds, radius, radius);
                if (_settings.PeakHold)
                {
                    var peakY = mirror ? height * .5 - _peaks[i] * height * .5 : height - _peaks[i] * height;
                    dc.DrawRoundedRectangle(_brush, null, new Rect(x, Math.Max(0, peakY - 2), barWidth, 2), 1, 1);
                }
            }
        }
        dc.Pop();
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
}

internal static class SpectrumBandMath
{
    public static void Map(float[] bins, int sampleRate, SpectrumSettings settings, float[] destination)
    {
        var nyquist = sampleRate * .5;
        var minimum = Math.Min(settings.MinFrequency, nyquist * .95);
        var maximum = Math.Min(settings.MaxFrequency, nyquist);
        var fftSize = (bins.Length - 1) * 2;
        for (var band = 0; band < destination.Length; band++)
        {
            var low = minimum * Math.Pow(maximum / minimum, band / (double)destination.Length);
            var high = minimum * Math.Pow(maximum / minimum, (band + 1.0) / destination.Length);
            var first = Math.Clamp((int)Math.Floor(low * fftSize / sampleRate), 1, bins.Length - 1);
            var last = Math.Clamp((int)Math.Ceiling(high * fftSize / sampleRate), first, bins.Length - 1);
            var amplitude = 0f;
            for (var i = first; i <= last; i++) if (float.IsFinite(bins[i])) amplitude = Math.Max(amplitude, bins[i]);
            destination[band] = (float)Math.Clamp(Math.Log10(1 + Math.Max(0, amplitude) * 60 * settings.Gain) / Math.Log10(61), 0, 1);
        }
    }
}
