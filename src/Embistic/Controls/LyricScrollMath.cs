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
}
