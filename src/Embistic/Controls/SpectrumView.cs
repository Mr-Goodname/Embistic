using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Embistic.Models;

namespace Embistic.Controls;

/// <summary>Native FFT drawing synchronized with the display's composition frames.</summary>
public sealed class SpectrumView : FrameworkElement
{
    private SpectrumSettings _settings = new();
    private float[]? _pending, _latest;
    private float[] _bands = new float[64], _target = new float[64], _peaks = new float[64];
    private int _sampleRate = 48000, _wakeQueued;
    private long _dataAt, _frameAt;
    private double _nextFrameAt;
    private bool _renderSubscribed, _mappingDirty = true;
    private TimeSpan _compositionTime = TimeSpan.MinValue;
    private LinearGradientBrush _brush = new();
    private Pen _linePen = new(), _wavePen = new(), _radialBarPen = new(), _radialPeakPen = new();
    public event Action? SettingsRequested;
    public int RenderedFrameCount { get; private set; }
    internal IReadOnlyList<float> DisplayedBands => _bands;
    internal bool IsRendering => _renderSubscribed;

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(SpectrumView), new PropertyMetadata(false, OnPlaybackChanged));
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }

    public static readonly DependencyProperty IsRadialProperty = DependencyProperty.Register(
        nameof(IsRadial), typeof(bool), typeof(SpectrumView), new FrameworkPropertyMetadata(false,
            FrameworkPropertyMetadataOptions.AffectsRender, (obj, _) =>
            {
                var view = (SpectrumView)obj;
                view.Height = view.IsRadial ? double.NaN : view._settings.Height;
                view.RebuildBrush();
            }));
    public bool IsRadial { get => (bool)GetValue(IsRadialProperty); set => SetValue(IsRadialProperty, value); }

    public SpectrumView()
    {
        Height = 90;
        Focusable = false;
        Loaded += (_, _) => Wake();
        Unloaded += (_, _) => { StopRendering(); Interlocked.Exchange(ref _pending, null); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Wake(); else StopRendering(); };
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
        if (!IsRadial) Height = _settings.Height;
        _mappingDirty = true;
        _nextFrameAt = Stopwatch.GetTimestamp();
        if (_bands.Length != _settings.BarCount)
        {
            _bands = new float[_settings.BarCount]; _target = new float[_settings.BarCount]; _peaks = new float[_settings.BarCount];
        }
        Effect = _settings.GlowEnabled ? new DropShadowEffect
        {
            Color = Parse(_settings.ColorEnd), BlurRadius = _settings.GlowRadius, ShadowDepth = 0, Opacity = .5
        } : null;
        RebuildBrush();
        if (!_settings.Enabled) { StopRendering(); Array.Clear(_bands); Array.Clear(_target); Array.Clear(_peaks); }
        else Wake();
        InvalidateVisual();
    }

    private static void OnPlaybackChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        var view = (SpectrumView)obj;
        if ((bool)args.NewValue) view.Wake();
        else
        {
            view.StopRendering(); view._latest = null; Interlocked.Exchange(ref view._pending, null);
            Array.Clear(view._bands); Array.Clear(view._target); Array.Clear(view._peaks); view.InvalidateVisual();
        }
    }

    private void Wake()
    {
        if (_renderSubscribed || !IsLoaded || !IsVisible || !IsPlaying || !_settings.Enabled) return;
        var now = Stopwatch.GetTimestamp();
        _frameAt = now - (long)(Stopwatch.Frequency / (double)_settings.FrameRate);
        _nextFrameAt = now;
        _compositionTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnCompositionRendering;
        _renderSubscribed = true;
    }

    private void StopRendering()
    {
        if (!_renderSubscribed) return;
        CompositionTarget.Rendering -= OnCompositionRendering;
        _renderSubscribed = false;
    }

    private void OnCompositionRendering(object? sender, EventArgs args)
    {
        if (!IsLoaded || !IsVisible || !IsPlaying || !_settings.Enabled) { StopRendering(); return; }
        if (args is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _compositionTime) return;
            _compositionTime = rendering.RenderingTime;
        }
        var now = Stopwatch.GetTimestamp();
        if (now + Stopwatch.Frequency * .00075 < _nextFrameAt) return;
        DrawFrame(now);
        // Accumulate deadlines so an early composition tick does not halve the
        // cadence. The monitor remains the upper limit for the selected fps.
        _nextFrameAt += Stopwatch.Frequency / (double)_settings.FrameRate;
        if (_nextFrameAt <= now) _nextFrameAt = now + Stopwatch.Frequency / (double)_settings.FrameRate;
    }

    private void DrawFrame(long now)
    {
        var frame = Interlocked.Exchange(ref _pending, null);
        if (frame is not null) { _latest = frame; _mappingDirty = true; }
        var delta = Math.Clamp((now - _frameAt) / (double)Stopwatch.Frequency, .001, .2);
        _frameAt = now;
        var stale = (now - Volatile.Read(ref _dataAt)) / (double)Stopwatch.Frequency > .45;
        if (stale || _latest is null) Array.Clear(_target);
        else if (_mappingDirty)
        {
            SpectrumBandMath.Map(_latest, Volatile.Read(ref _sampleRate), _settings, _target);
            _mappingDirty = false;
        }
        var blend = SpectrumMotionMath.Blend(delta, _settings.Smoothing);
        var active = false;
        for (var i = 0; i < _bands.Length; i++)
        {
            _bands[i] += (_target[i] - _bands[i]) * blend;
            if (_bands[i] < .0005f) _bands[i] = 0;
            _peaks[i] = Math.Max(_bands[i], _peaks[i] - (float)(delta * .38));
            active |= _bands[i] > 0 || (_settings.PeakHold && _peaks[i] > 0);
        }
        RenderedFrameCount++;
        InvalidateVisual();
        if (stale && !active) StopRendering();
    }

    private void RebuildBrush()
    {
        _brush = new LinearGradientBrush(Parse(_settings.ColorStart), Parse(_settings.ColorEnd), 0)
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, ActualHeight),
            EndPoint = new Point(Math.Max(1, ActualWidth), 0)
        };
        _brush.Freeze();
        _linePen = MakePen(2, true); _wavePen = MakePen(.8, true);
        var radialWidth = Math.Max(.7, Math.Min(ActualWidth, ActualHeight) * .7 * Math.PI / _bands.Length * _settings.BarWidthRatio);
        _radialBarPen = MakePen(radialWidth, _settings.RoundedBars);
        _radialPeakPen = MakePen(Math.Min(radialWidth, 2), true);
    }

    private Pen MakePen(double width, bool rounded)
    {
        var pen = new Pen(_brush, width)
        {
            StartLineCap = rounded ? PenLineCap.Round : PenLineCap.Flat,
            EndLineCap = rounded ? PenLineCap.Round : PenLineCap.Flat,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze(); return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!_settings.Enabled || ActualWidth <= 0 || ActualHeight <= 0) return;
        dc.PushOpacity(_settings.Opacity);
        if (IsRadial) DrawRadial(dc);
        else DrawCartesian(dc);
        dc.Pop();
    }

    private void DrawCartesian(DrawingContext dc)
    {
        var width = ActualWidth / _bands.Length;
        var height = Math.Max(1, ActualHeight - 3);
        if (_settings.Mode is "Wave" or "Line")
        {
            var filled = _settings.Mode == "Wave";
            var geometry = SpectrumCurveMath.Cartesian(_bands, ActualWidth, height, filled);
            dc.DrawGeometry(filled ? _brush : null, filled ? _wavePen : _linePen, geometry);
            return;
        }
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

    private void DrawRadial(DrawingContext dc)
    {
        var center = new Point(ActualWidth * .5, ActualHeight * .5);
        var outerRadius = Math.Max(1, Math.Min(ActualWidth, ActualHeight) * .5 - 4);
        var innerRadius = outerRadius * .72;
        var extent = outerRadius - innerRadius;
        if (_settings.Mode is "Wave" or "Line")
        {
            var filled = _settings.Mode == "Wave";
            var geometry = SpectrumCurveMath.Radial(_bands, center, innerRadius, outerRadius, filled);
            dc.DrawGeometry(filled ? _brush : null, filled ? _wavePen : _linePen, geometry);
            return;
        }
        var mirror = _settings.Mode == "Mirror";
        for (var i = 0; i < _bands.Length; i++)
        {
            if (_bands[i] <= .001f) continue;
            var angle = i * Math.PI * 2 / _bands.Length - Math.PI * .5;
            var start = mirror ? innerRadius + extent * .5 * (1 - _bands[i]) : innerRadius;
            var end = mirror ? innerRadius + extent * .5 * (1 + _bands[i]) : innerRadius + extent * _bands[i];
            dc.DrawLine(_radialBarPen, SpectrumCurveMath.Polar(center, start, angle), SpectrumCurveMath.Polar(center, end, angle));
            if (_settings.PeakHold)
            {
                var radius = mirror ? innerRadius + extent * .5 * (1 + _peaks[i]) : innerRadius + extent * _peaks[i];
                dc.DrawLine(_radialPeakPen, SpectrumCurveMath.Polar(center, Math.Max(innerRadius, radius - 1), angle), SpectrumCurveMath.Polar(center, radius + 1, angle));
            }
        }
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
}

internal static class SpectrumMotionMath
{
    public static float Blend(double seconds, double smoothing)
    {
        // Short interpolation bridges FFT packets even at zero response smoothing.
        // Higher smoothing preserves the original time-based response at any fps.
        var response = smoothing <= 0 ? .022 : Math.Max(.022, -1 / (30 * Math.Log(smoothing)));
        return (float)(1 - Math.Exp(-seconds / response));
    }
}

internal static class SpectrumCurveMath
{
    public static StreamGeometry Cartesian(IReadOnlyList<float> bands, double width, double height, bool filled)
    {
        var smooth = SmoothAmplitudes(bands, false);
        var points = new Point[bands.Count + 2];
        points[0] = new Point(0, height); points[^1] = new Point(width, height);
        for (var i = 0; i < bands.Count; i++)
            points[i + 1] = new Point((i + .5) * width / bands.Count, height * (1 - smooth[i]));
        var slopes = new double[points.Length - 1];
        for (var i = 0; i < slopes.Length; i++) slopes[i] = (points[i + 1].Y - points[i].Y) / (points[i + 1].X - points[i].X);
        var tangents = new double[points.Length];
        for (var i = 1; i < tangents.Length - 1; i++) tangents[i] = MonotoneTangent(slopes[i - 1], slopes[i]);
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], filled, filled);
            for (var i = 0; i < points.Length - 1; i++)
            {
                var dx = (points[i + 1].X - points[i].X) / 3;
                path.BezierTo(new Point(points[i].X + dx, points[i].Y + tangents[i] * dx),
                    new Point(points[i + 1].X - dx, points[i + 1].Y - tangents[i + 1] * dx), points[i + 1], true, true);
            }
        }
        geometry.Freeze(); return geometry;
    }

    public static StreamGeometry Radial(IReadOnlyList<float> bands, Point center, double innerRadius, double outerRadius, bool filled)
    {
        var count = bands.Count;
        var angleStep = 2 * Math.PI / count;
        var smooth = SmoothAmplitudes(bands, true);
        var radii = new double[count]; var tangents = new double[count];
        for (var i = 0; i < count; i++) radii[i] = innerRadius + (outerRadius - innerRadius) * smooth[i];
        for (var i = 0; i < count; i++) tangents[i] = MonotoneTangent(
            (radii[i] - radii[(i + count - 1) % count]) / angleStep,
            (radii[(i + 1) % count] - radii[i]) / angleStep);
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var path = geometry.Open())
        {
            path.BeginFigure(Polar(center, radii[0], -Math.PI * .5), filled, true);
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % count;
                var angle = i * angleStep - Math.PI * .5;
                var endAngle = angle + angleStep;
                var p0 = Polar(center, radii[i], angle); var p1 = Polar(center, radii[next], endAngle);
                var d0 = Derivative(radii[i], tangents[i], angle) * (angleStep / 3);
                var d1 = Derivative(radii[next], tangents[next], endAngle) * (angleStep / 3);
                path.BezierTo(p0 + d0, p1 - d1, p1, true, true);
            }
            if (filled)
            {
                path.BeginFigure(new Point(center.X, center.Y - innerRadius), true, true);
                path.ArcTo(new Point(center.X, center.Y + innerRadius), new Size(innerRadius, innerRadius), 0, false, SweepDirection.Clockwise, true, true);
                path.ArcTo(new Point(center.X, center.Y - innerRadius), new Size(innerRadius, innerRadius), 0, false, SweepDirection.Clockwise, true, true);
            }
        }
        geometry.Freeze(); return geometry;
    }

    private static double[] SmoothAmplitudes(IReadOnlyList<float> bands, bool closed)
    {
        // Spatial smoothing is separate from the response slider. A lone FFT peak
        // becomes a rounded hill rather than a narrow tooth, with a similar visual
        // width at different band counts. This remains a convex average of real data.
        var sigma = Math.Max(.8, bands.Count / 64.0);
        var radius = (int)Math.Ceiling(sigma * 3);
        var weights = new double[radius * 2 + 1]; var total = 0.0;
        for (var offset = -radius; offset <= radius; offset++)
        {
            var weight = Math.Exp(-offset * offset / (2 * sigma * sigma));
            weights[offset + radius] = weight; total += weight;
        }
        var smooth = new double[bands.Count];
        for (var i = 0; i < bands.Count; i++)
        {
            for (var offset = -radius; offset <= radius; offset++)
            {
                var source = closed ? (i + offset + bands.Count) % bands.Count : Math.Clamp(i + offset, 0, bands.Count - 1);
                smooth[i] += bands[source] * weights[offset + radius];
            }
            smooth[i] /= total;
        }
        return smooth;
    }

    // A sign change gives a horizontal tangent at a peak or trough. The harmonic
    // mean bounds each control point between neighboring samples, preventing spikes.
    private static double MonotoneTangent(double left, double right) =>
        left * right <= 0 ? 0 : 2 * left * right / (left + right);

    public static Point Polar(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
    private static Vector Derivative(double radius, double radialDerivative, double angle) =>
        new(radialDerivative * Math.Cos(angle) - radius * Math.Sin(angle), radialDerivative * Math.Sin(angle) + radius * Math.Cos(angle));
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
