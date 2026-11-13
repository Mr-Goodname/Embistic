namespace Embistic.Controls;

/// <summary>Viewport calculations kept separate from rendering and animation clocks.</summary>
public static class LyricScrollMath
{
    public static double CenterOffset(double lineTop, double lineHeight, double viewportHeight, double extentHeight)
    {
        if (!double.IsFinite(lineTop) || !double.IsFinite(lineHeight) || !double.IsFinite(viewportHeight) || !double.IsFinite(extentHeight)) return 0;
        return Math.Clamp(lineTop + Math.Max(0, lineHeight) * .5 - Math.Max(0, viewportHeight) * .5, 0, Math.Max(0, extentHeight - viewportHeight));
    }

    public static bool IsInViewport(double lineTop, double lineHeight, double offset, double viewportHeight) =>
        viewportHeight > 0 && lineHeight > 0 && lineTop + lineHeight > offset && lineTop < offset + viewportHeight;

    public static double FocusScale(int distance, double focusScale) => Math.Max(.72, 1 - Math.Min(5, Math.Abs(distance)) * .055) * (distance == 0 ? focusScale : 1);
    public static double EaseOut(double progress) => 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);
}

/// <summary>Interpolates between audio position observations, freezes while paused, and rebases immediately on seeks.</summary>
public sealed class LyricPlaybackClock
{
    private double _position;
    private long _observedAt;
    private long _lastObservedPosition;
    public bool IsPlaying { get; private set; }
    public void Update(long positionMs, bool playing, bool forceRebase = false)
    {
        if (!forceRebase && playing && IsPlaying && positionMs == _lastObservedPosition) return;
        _position = positionMs;
        _lastObservedPosition = positionMs;
        _observedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        IsPlaying = playing;
    }
    public double PositionMs => _position + (IsPlaying && _observedAt != 0 ? Math.Min(1200, System.Diagnostics.Stopwatch.GetElapsedTime(_observedAt).TotalMilliseconds) : 0);
}
