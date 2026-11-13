using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Embistic.Models;

namespace Embistic.Services;

internal static class Motion
{
    public static void Enter(FrameworkElement element, LyricAppearanceSettings appearance, bool page = false)
    {
        var move = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = move;
        var enabled = appearance.AnimationsEnabled && SystemParameters.ClientAreaAnimation
            && (page ? appearance.PageTransitionsEnabled : appearance.LyricTransition != "None");
        element.BeginAnimation(UIElement.OpacityProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 1;
        move.Y = 0;
        if (!enabled || !element.IsVisible) return;
        var duration = TimeSpan.FromMilliseconds(page ? Math.Min(appearance.TransitionDurationMs, 500) : appearance.TransitionDurationMs);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
        if (page || appearance.LyricTransition == "Slide")
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(page ? 14 : 9, 0, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }
}
