using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Embistic.Controls;

/// <summary>Allows a drag to start anywhere on a slider, including its rail.</summary>
public static class SliderDragBehavior
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SliderDragBehavior), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty RailDraggingProperty = DependencyProperty.RegisterAttached(
        "RailDragging", typeof(bool), typeof(SliderDragBehavior), new PropertyMetadata(false));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    private static void EnabledChanged(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not Slider slider) return;
        if ((bool)e.NewValue)
        {
            // Slider's move-to-point class handler handles rail presses before
            // instance handlers run. Observe handled input to extend that press
            // into a capture-backed drag.
            slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down), true);
            slider.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move), true);
            slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up), true);
            slider.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(Lost), true);
        }
        else
        {
            slider.SetValue(RailDraggingProperty, false);
            if (slider.IsMouseCaptured) slider.ReleaseMouseCapture();
            slider.RemoveHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(Down));
            slider.RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(Move));
            slider.RemoveHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(Up));
            slider.RemoveHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(Lost));
        }
    }
    private static void Down(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!slider.IsEnabled || !slider.IsMoveToPointEnabled) return;
        slider.ApplyTemplate();
        if (slider.Template.FindName("PART_Track", slider) is not System.Windows.Controls.Primitives.Track track) return;
        if (track.Thumb is not { } thumb || thumb.IsMouseOver) return; // Keep native Thumb drag and keyboard behavior.
        slider.Focus();
        if (!slider.CaptureMouse()) return;
        slider.SetValue(RailDraggingProperty, true);
        Update(slider, e);
        e.Handled = true;
    }
    private static void Move(object sender, MouseEventArgs e)
    {
        var slider = (Slider)sender;
        if (!(bool)slider.GetValue(RailDraggingProperty)) return;
        if (e.LeftButton != MouseButtonState.Pressed) { slider.ReleaseMouseCapture(); return; }
        Update(slider, e);
        e.Handled = true;
    }
    private static void Up(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (!(bool)slider.GetValue(RailDraggingProperty)) return;
        Update(slider, e);
        slider.SetValue(RailDraggingProperty, false);
        slider.ReleaseMouseCapture();
    }
    private static void Lost(object sender, MouseEventArgs e) => ((Slider)sender).SetValue(RailDraggingProperty, false);
    private static void Update(Slider slider, MouseEventArgs e)
    {
        if (slider.Template.FindName("PART_Track", slider) is not System.Windows.Controls.Primitives.Track track) return;
        if (track.Thumb is not { } thumb) return;
        var point = e.GetPosition(track);
        var thumbSize = slider.Orientation == Orientation.Horizontal ? thumb.ActualWidth : thumb.ActualHeight;
        var length = (slider.Orientation == Orientation.Horizontal ? track.ActualWidth : track.ActualHeight) - thumbSize;
        if (length <= 0) return;
        var fraction = ((slider.Orientation == Orientation.Horizontal ? point.X : point.Y) - thumbSize * .5) / length;
        if (slider.Orientation == Orientation.Horizontal ? slider.IsDirectionReversed : !slider.IsDirectionReversed) fraction = 1 - fraction;
        // ValueFromPoint uses the current Thumb center. Immediately after native
        // move-to-point handling that center has not been arranged yet, which
        // would apply the same jump twice. Absolute rail coordinates stay stable.
        var value = slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
        if (!double.IsFinite(value)) return;
        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        if (slider.IsSnapToTickEnabled)
        {
            var lower = slider.Minimum; var upper = slider.Maximum;
            if (slider.Ticks.Count > 0)
            {
                foreach (var tick in slider.Ticks)
                {
                    if (!double.IsFinite(tick) || tick < slider.Minimum || tick > slider.Maximum) continue;
                    if (tick <= value) lower = Math.Max(lower, tick);
                    if (tick >= value) upper = Math.Min(upper, tick);
                }
            }
            else if (slider.TickFrequency > 0)
            {
                lower = slider.Minimum + Math.Floor((value - slider.Minimum) / slider.TickFrequency) * slider.TickFrequency;
                upper = Math.Min(slider.Maximum, lower + slider.TickFrequency);
            }
            else { slider.SetCurrentValue(Slider.ValueProperty, value); return; }
            value = value - lower < upper - value ? lower : upper;
        }
        // Preserve a two-way binding while dragging from the rail.
        slider.SetCurrentValue(Slider.ValueProperty, value);
    }
}
