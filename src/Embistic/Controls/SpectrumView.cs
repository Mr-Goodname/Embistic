using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private volatile bool _renderSubscribed, _captureWakeEnabled;
    private bool _mappingDirty = true;
    private int _wakeDispatchCount;
    private readonly SpectrumCurveBuffer _curve = new();
    private readonly DrawingVisual _drawing = new();
    private TimeSpan _compositionTime = TimeSpan.MinValue;
    private LinearGradientBrush _brush = new();
    private Pen _linePen = new(), _wavePen = new(), _radialBarPen = new(), _radialPeakPen = new();
    public event Action? SettingsRequested;
    public int RenderedFrameCount { get; private set; }
    internal IReadOnlyList<float> DisplayedBands => _bands;
    internal bool IsRendering => _renderSubscribed;
    internal int WakeDispatchCount => Volatile.Read(ref _wakeDispatchCount);

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

    public static readonly DependencyProperty RadialShapeProperty = DependencyProperty.Register(
        nameof(RadialShape), typeof(string), typeof(SpectrumView), new FrameworkPropertyMetadata("Circle",
            FrameworkPropertyMetadataOptions.AffectsRender, OnContourChanged));
    public string RadialShape { get => (string)GetValue(RadialShapeProperty); set => SetValue(RadialShapeProperty, value); }

    // Measured from the edge of the host to the base contour. The spectrum grows
    // outward from that contour, leaving the whole cover/record unobscured.
    public static readonly DependencyProperty RadialInsetProperty = DependencyProperty.Register(
        nameof(RadialInset), typeof(double), typeof(SpectrumView), new FrameworkPropertyMetadata(double.NaN,
            FrameworkPropertyMetadataOptions.AffectsRender, OnContourChanged));
    public double RadialInset { get => (double)GetValue(RadialInsetProperty); set => SetValue(RadialInsetProperty, value); }

    public static readonly DependencyProperty RadialCornerRadiusProperty = DependencyProperty.Register(
        nameof(RadialCornerRadius), typeof(double), typeof(SpectrumView), new FrameworkPropertyMetadata(32d,
            FrameworkPropertyMetadataOptions.AffectsRender, OnContourChanged));
    public double RadialCornerRadius { get => (double)GetValue(RadialCornerRadiusProperty); set => SetValue(RadialCornerRadiusProperty, value); }

    private static void OnContourChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args) => ((SpectrumView)obj).RebuildBrush();

    public SpectrumView()
    {
        AddVisualChild(_drawing);
        Height = 90;
        Focusable = false;
        Loaded += (_, _) => { UpdateCaptureWakeState(); Wake(); };
        Unloaded += (_, _) => { _captureWakeEnabled = false; StopRendering(); Interlocked.Exchange(ref _pending, null); };
        IsVisibleChanged += (_, _) => { UpdateCaptureWakeState(); if (IsVisible) Wake(); else StopRendering(); };
        SizeChanged += (_, _) => { RebuildBrush(); RenderSpectrum(); };
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
        // Active views already read the latest packet at composition time. Do not
        // queue five render-priority callbacks per FFT packet (including hidden
        // placements); only a visible, playing view asleep after silence needs one.
        if (_captureWakeEnabled && !_renderSubscribed && Interlocked.Exchange(ref _wakeQueued, 1) == 0)
        {
            Interlocked.Increment(ref _wakeDispatchCount);
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { Interlocked.Exchange(ref _wakeQueued, 0); Wake(); });
        }
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
        UpdateCaptureWakeState();
        if (!_settings.Enabled) { StopRendering(); Array.Clear(_bands); Array.Clear(_target); Array.Clear(_peaks); }
        else Wake();
        RenderSpectrum();
    }

    private static void OnPlaybackChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        var view = (SpectrumView)obj;
        view.UpdateCaptureWakeState();
        if ((bool)args.NewValue) view.Wake();
        else
        {
            view.StopRendering(); view._latest = null; Interlocked.Exchange(ref view._pending, null);
            Array.Clear(view._bands); Array.Clear(view._target); Array.Clear(view._peaks); view.RenderSpectrum();
        }
    }

    private void UpdateCaptureWakeState() => _captureWakeEnabled = IsLoaded && IsVisible && IsPlaying && _settings.Enabled;

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
            SpectrumBandMath.MapShared(_latest, Volatile.Read(ref _sampleRate), _settings, _target);
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
        RenderSpectrum();
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
        GetContourDimensions(out var inner, out _, out var corner);
        var perimeter = SpectrumContourMath.Perimeter(RadialShape, inner, corner);
        var radialWidth = Math.Max(.7, perimeter / _bands.Length * _settings.BarWidthRatio);
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

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _drawing : throw new ArgumentOutOfRangeException(nameof(index));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        RenderSpectrum();
    }

    private void RenderSpectrum()
    {
        // Updating a DrawingVisual sends only drawing changes to composition.
        // FrameworkElement.InvalidateVisual also invalidates Arrange; doing that
        // for every spectrum on every frame repeatedly wakes WPF's layout queue.
        using var dc = _drawing.RenderOpen();
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
            var geometry = _curve.Cartesian(_bands, ActualWidth, height, filled);
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
        GetContourDimensions(out var innerRadius, out var outerRadius, out var cornerRadius);
        var extent = outerRadius - innerRadius;
        _curve.PrepareContour(_bands.Length, center, RadialShape, innerRadius, cornerRadius);
        if (_settings.Mode is "Wave" or "Line")
        {
            var filled = _settings.Mode == "Wave";
            var geometry = _curve.Radial(_bands, center, innerRadius, outerRadius, filled, RadialShape, cornerRadius);
            dc.DrawGeometry(filled ? _brush : null, filled ? _wavePen : _linePen, geometry);
            return;
        }
        var mirror = _settings.Mode == "Mirror";
        for (var i = 0; i < _bands.Length; i++)
        {
            if (_bands[i] <= .001f) continue;
            var sample = _curve.Contour[i];
            var start = mirror ? extent * .5 * (1 - _bands[i]) : 0;
            var end = mirror ? extent * .5 * (1 + _bands[i]) : extent * _bands[i];
            dc.DrawLine(_radialBarPen, sample.Position + sample.Normal * start, sample.Position + sample.Normal * end);
            if (_settings.PeakHold)
            {
                var distance = mirror ? extent * .5 * (1 + _peaks[i]) : extent * _peaks[i];
                dc.DrawLine(_radialPeakPen, sample.Position + sample.Normal * Math.Max(0, distance - 1), sample.Position + sample.Normal * (distance + 1));
            }
        }
    }

    private void GetContourDimensions(out double inner, out double outer, out double corner)
    {
        var half = Math.Max(1, Math.Min(ActualWidth, ActualHeight) * .5);
        outer = Math.Max(1, half - 4);
        inner = double.IsFinite(RadialInset) ? Math.Clamp(half - RadialInset, 1, outer) : outer * .72;
        corner = double.IsFinite(RadialCornerRadius) ? Math.Clamp(RadialCornerRadius, .1, inner) : Math.Min(32, inner);
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
    public static StreamGeometry Cartesian(IReadOnlyList<float> bands, double width, double height, bool filled) =>
        new SpectrumCurveBuffer().Cartesian(bands, width, height, filled);

    public static StreamGeometry Radial(IReadOnlyList<float> bands, Point center, double innerRadius, double outerRadius, bool filled) =>
        new SpectrumCurveBuffer().Radial(bands, center, innerRadius, outerRadius, filled);

    public static Point Polar(Point center, double radius, double angle) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
}

/// <summary>Per-view scratch storage: no convolution tables or point arrays are allocated per animation frame.</summary>
internal sealed class SpectrumCurveBuffer
{
    private double[] _smooth = [], _weights = [], _slopes = [], _tangents = [];
    private int[] _openIndices = [], _closedIndices = [];
    private Point[] _points = [];
    private SpectrumContourSample[] _contour = [];
    private Point _contourCenter;
    private string _contourShape = "";
    private double _contourRadius, _contourCorner, _contourStep;
    public IReadOnlyList<SpectrumContourSample> Contour => _contour;

    public StreamGeometry Cartesian(IReadOnlyList<float> bands, double width, double height, bool filled)
    {
        SmoothAmplitudes(bands, false);
        var smooth = _smooth; var points = _points; var slopes = _slopes; var tangents = _tangents;
        points[0] = new Point(0, height); points[^1] = new Point(width, height);
        for (var i = 0; i < bands.Count; i++)
            points[i + 1] = new Point((i + .5) * width / bands.Count, height * (1 - smooth[i]));
        for (var i = 0; i < slopes.Length; i++) slopes[i] = (points[i + 1].Y - points[i].Y) / (points[i + 1].X - points[i].X);
        tangents[0] = tangents[^1] = 0;
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

    public StreamGeometry Radial(IReadOnlyList<float> bands, Point center, double innerRadius, double outerRadius, bool filled,
        string shape = "Circle", double cornerRadius = 32)
    {
        var count = bands.Count;
        SmoothAmplitudes(bands, true);
        PrepareContour(count, center, shape, innerRadius, cornerRadius);
        var amplitudes = _smooth; var tangents = _tangents;
        var extent = Math.Max(0, outerRadius - innerRadius);
        for (var i = 0; i < count; i++) amplitudes[i] *= extent;
        for (var i = 0; i < count; i++) tangents[i] = MonotoneTangent(
            (amplitudes[i] - amplitudes[(i + count - 1) % count]) / _contourStep,
            (amplitudes[(i + 1) % count] - amplitudes[i]) / _contourStep);
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var path = geometry.Open())
        {
            path.BeginFigure(_contour[0].Position + _contour[0].Normal * amplitudes[0], filled, true);
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % count;
                var a = _contour[i]; var b = _contour[next];
                var p0 = a.Position + a.Normal * amplitudes[i]; var p1 = b.Position + b.Normal * amplitudes[next];
                var d0 = (a.Tangent * (1 + a.Curvature * amplitudes[i]) + a.Normal * tangents[i]) * (_contourStep / 3);
                var d1 = (b.Tangent * (1 + b.Curvature * amplitudes[next]) + b.Normal * tangents[next]) * (_contourStep / 3);
                path.BezierTo(p0 + d0, p1 - d1, p1, true, true);
            }
            if (filled) SpectrumContourMath.AddHole(path, center, shape, innerRadius, cornerRadius);
        }
        geometry.Freeze(); return geometry;
    }

    public void PrepareContour(int count, Point center, string shape, double radius, double corner)
    {
        if (_contour.Length == count && _contourCenter == center && _contourShape == shape && _contourRadius == radius && _contourCorner == corner) return;
        if (_contour.Length != count) _contour = new SpectrumContourSample[count];
        _contourCenter = center; _contourShape = shape; _contourRadius = radius; _contourCorner = corner;
        _contourStep = SpectrumContourMath.Perimeter(shape, radius, corner) / count;
        for (var i = 0; i < count; i++) _contour[i] = SpectrumContourMath.Sample(center, shape, radius, corner, i / (double)count);
    }

    private void SmoothAmplitudes(IReadOnlyList<float> bands, bool closed)
    {
        if (_smooth.Length != bands.Count) Resize(bands.Count);
        var indices = closed ? _closedIndices : _openIndices;
        for (var i = 0; i < bands.Count; i++)
        {
            var sum = 0d; var start = i * _weights.Length;
            for (var j = 0; j < _weights.Length; j++) sum += bands[indices[start + j]] * _weights[j];
            _smooth[i] = sum;
        }
    }

    private void Resize(int count)
    {
        _smooth = new double[count]; _points = new Point[count + 2]; _slopes = new double[count + 1]; _tangents = new double[count + 2];
        // Spatial smoothing is separate from the response slider. A lone FFT peak
        // becomes a rounded hill rather than a narrow tooth, with a similar visual
        // width at different band counts. This remains a convex average of real data.
        var sigma = Math.Max(.8, count / 64.0);
        var radius = (int)Math.Ceiling(sigma * 3);
        _weights = new double[radius * 2 + 1]; var total = 0.0;
        _openIndices = new int[count * _weights.Length]; _closedIndices = new int[_openIndices.Length];
        for (var offset = -radius; offset <= radius; offset++)
        {
            var weight = Math.Exp(-offset * offset / (2 * sigma * sigma));
            _weights[offset + radius] = weight; total += weight;
        }
        for (var i = 0; i < _weights.Length; i++) _weights[i] /= total;
        for (var i = 0; i < count; i++)
        {
            for (var offset = -radius; offset <= radius; offset++)
            {
                var index = i * _weights.Length + offset + radius;
                _closedIndices[index] = (i + offset + count) % count;
                _openIndices[index] = Math.Clamp(i + offset, 0, count - 1);
            }
        }
    }

    // A sign change gives a horizontal tangent at a peak or trough. The harmonic
    // mean bounds each control point between neighboring samples, preventing spikes.
    private static double MonotoneTangent(double left, double right) =>
        left * right <= 0 ? 0 : 2 * left * right / (left + right);

}

internal readonly record struct SpectrumContourSample(Point Position, Vector Normal, Vector Tangent, double Curvature);

internal static class SpectrumContourMath
{
    public static double Perimeter(string shape, double radius, double corner) => shape == "RoundedSquare"
        ? 8 * (radius - Math.Clamp(corner, .1, radius)) + 2 * Math.PI * Math.Clamp(corner, .1, radius)
        : 2 * Math.PI * radius;

    public static SpectrumContourSample Sample(Point center, string shape, double radius, double corner, double fraction)
    {
        if (shape != "RoundedSquare")
        {
            var angle = fraction * Math.PI * 2 - Math.PI * .5;
            var normal = new Vector(Math.Cos(angle), Math.Sin(angle));
            return new(center + normal * radius, normal, new Vector(-normal.Y, normal.X), 1 / radius);
        }
        corner = Math.Clamp(corner, .1, radius);
        var flat = radius - corner; var quarter = flat * 2 + Math.PI * corner * .5;
        var distance = ((fraction % 1 + 1) % 1 * quarter * 4 + flat) % (quarter * 4);
        var side = Math.Min(3, (int)(distance / quarter)); var local = distance - side * quarter;
        Point point; Vector outward, tangent; double curvature;
        if (local <= flat * 2)
        {
            point = new Point(-flat + local, -radius); outward = new Vector(0, -1); tangent = new Vector(1, 0); curvature = 0;
        }
        else
        {
            var angle = -Math.PI * .5 + (local - flat * 2) / corner;
            outward = new Vector(Math.Cos(angle), Math.Sin(angle)); tangent = new Vector(-outward.Y, outward.X);
            point = new Point(flat + corner * outward.X, -flat + corner * outward.Y); curvature = 1 / corner;
        }
        for (var turn = 0; turn < side; turn++)
        {
            point = new Point(-point.Y, point.X); outward = new Vector(-outward.Y, outward.X); tangent = new Vector(-tangent.Y, tangent.X);
        }
        return new(center + (Vector)point, outward, tangent, curvature);
    }

    public static void AddHole(StreamGeometryContext path, Point center, string shape, double radius, double corner)
    {
        if (shape != "RoundedSquare")
        {
            path.BeginFigure(new Point(center.X, center.Y - radius), true, true);
            path.ArcTo(new Point(center.X, center.Y + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            path.ArcTo(new Point(center.X, center.Y - radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, true);
            return;
        }
        corner = Math.Clamp(corner, .1, radius); var flat = radius - corner;
        path.BeginFigure(new Point(center.X - flat, center.Y - radius), true, true);
        path.LineTo(new Point(center.X + flat, center.Y - radius), true, false);
        path.ArcTo(new Point(center.X + radius, center.Y - flat), new Size(corner, corner), 0, false, SweepDirection.Clockwise, true, true);
        path.LineTo(new Point(center.X + radius, center.Y + flat), true, false);
        path.ArcTo(new Point(center.X + flat, center.Y + radius), new Size(corner, corner), 0, false, SweepDirection.Clockwise, true, true);
        path.LineTo(new Point(center.X - flat, center.Y + radius), true, false);
        path.ArcTo(new Point(center.X - radius, center.Y + flat), new Size(corner, corner), 0, false, SweepDirection.Clockwise, true, true);
        path.LineTo(new Point(center.X - radius, center.Y - flat), true, false);
        path.ArcTo(new Point(center.X - flat, center.Y - radius), new Size(corner, corner), 0, false, SweepDirection.Clockwise, true, true);
    }
}

internal static class SpectrumBandMath
{
    private static readonly ConditionalWeakTable<float[], MappedFrame> Frames = new();
    private static int _sharedMappingCount;
    internal static int SharedMappingCount => Volatile.Read(ref _sharedMappingCount);
    private readonly record struct MapKey(int SampleRate, int Count, double Minimum, double Maximum, double Gain);
    private sealed class MappedFrame { public readonly Dictionary<MapKey, float[]> Mappings = new(); }

    // A published FFT array is immutable. All selected placements can reuse its
    // mapped amplitudes; weak keys release them as soon as the last view moves on.
    public static void MapShared(float[] bins, int sampleRate, SpectrumSettings settings, float[] destination)
    {
        var frame = Frames.GetOrCreateValue(bins);
        var key = new MapKey(sampleRate, destination.Length, settings.MinFrequency, settings.MaxFrequency, settings.Gain);
        lock (frame)
        {
            if (!frame.Mappings.TryGetValue(key, out var result))
            {
                result = new float[destination.Length]; Map(bins, sampleRate, settings, result);
                // A preview may drag many settings while a silent frame is retained.
                if (frame.Mappings.Count >= 8) frame.Mappings.Clear();
                frame.Mappings.Add(key, result); Interlocked.Increment(ref _sharedMappingCount);
            }
            result.CopyTo(destination, 0);
        }
    }
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
