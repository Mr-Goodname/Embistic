using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Embistic.Models;
using Embistic.Services;

namespace Embistic.Controls;

/// <summary>
/// A native, pixel-scrolling lyric view. Text is measured at its natural wrapped height;
/// render transforms never disturb those measurements. Only visible changing rows animate.
/// </summary>
public partial class AnimatedLyricsView : UserControl
{
    private readonly List<LineVisual> _rows = [];
    private readonly DispatcherTimer _followTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LyricsDocument _document = LyricsDocument.Empty();
    private LyricAppearanceSettings _appearance = new();
    private Color _currentColor = Color.FromRgb(0xBF, 0xD7, 0xFF);
    private Color _inactiveColor = Color.FromRgb(0x82, 0x96, 0xB2);
    private Color _translationColor = Color.FromRgb(0xC2, 0xCD, 0xDF);
    private int _activeIndex = -1, _lastActivationIndex = -1;
    private long _positionMs, _lastMotionAt, _manualInputAt, _lastActivationAt;
    private bool _hasPosition, _manualScrolling, _layoutScheduled, _animateAfterLayout;
    private double _offsetMs, _targetScrollOffset;
    private readonly LyricPlaybackClock _clock = new();
    private readonly TimeProvider _scrollTime;
    private bool _renderAttached, _scrollMoving;
    private long _scrollStarted;
    private double _scrollFrom, _scrollDuration;
    private double _visualOffset;
    private bool _ownsVisualOffset;
    private TimeSpan _lastRenderingTime;
    private int _karaokePreparationVersion;

    public event Action<long>? SeekRequested;
    public IReadOnlyList<LyricsLine> Lines => _document.Lines;
    public int ActiveLineIndex => _activeIndex;
    public bool IsFollowingPlayback => !_manualScrolling;
    public double CurrentScrollOffset => _ownsVisualOffset ? _visualOffset : Scroller.VerticalOffset;
    public double TargetScrollOffset => _targetScrollOffset;

    public AnimatedLyricsView() : this(TimeProvider.System) { }

    // Keep rendering and audio observation on their existing clocks. The scroll
    // timeline can be controlled independently when verifying a slow re-layout.
    internal AnimatedLyricsView(TimeProvider scrollTime)
    {
        _scrollTime = scrollTime ?? throw new ArgumentNullException(nameof(scrollTime));
        InitializeComponent();
        _followTimer.Tick += FollowTimer_Tick;
        Loaded += (_, _) => { StartRendering(); ScheduleLayoutFollow(); };
        Unloaded += (_, _) => { StopMotion(); StopRendering(); };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { StartRendering(); ScheduleLayoutFollow(); }
            else { StopMotion(); StopRendering(); }
        };
        SizeChanged += (_, _) => ScheduleLayoutFollow();
        Scroller.ScrollChanged += (_, e) =>
        {
            // A native scroll is committed only when a transition finishes. Keep the
            // same on-screen position until ScrollViewer has processed that commit.
            if (_ownsVisualOffset)
            {
                if (!_scrollMoving)
                {
                    _visualOffset = Math.Clamp(_visualOffset, 0, Math.Max(0, Scroller.ScrollableHeight));
                    _targetScrollOffset = _visualOffset;
                }
                ScrollTranslation.Y = Scroller.VerticalOffset - _visualOffset;
                if (!_scrollMoving && Math.Abs(Scroller.VerticalOffset - _visualOffset) < .1)
                {
                    _ownsVisualOffset = false;
                    ScrollTranslation.Y = 0;
                }
            }
            if (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0 || e.ExtentHeightChange != 0) ScheduleLayoutFollow();
        };
        Scroller.PreviewMouseWheel += ScrollWheel;
        Scroller.PreviewMouseDown += (_, e) => { if (IsScrollBarInput(e.OriginalSource as DependencyObject)) SuspendFollow(); };
        Scroller.PreviewMouseUp += (_, e) => { if (IsScrollBarInput(e.OriginalSource as DependencyObject)) SuspendFollow(); };
        Scroller.PreviewTouchDown += (_, _) => SuspendFollow();
        Scroller.PreviewTouchUp += (_, _) => SuspendFollow();
        PreviewKeyDown += ScrollKey;
        ApplyAppearance(_appearance);
    }

    public void SetDocument(LyricsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (ReferenceEquals(document, _document)) return;
        StopMotion();
        _karaokePreparationVersion++;
        _document = document;
        _clock.Update(0, false, forceRebase: true);
        _activeIndex = -1;
        _lastActivationIndex = -1;
        _lastActivationAt = 0;
        _hasPosition = false;
        _lastMotionAt = 0;
        _manualScrolling = false;
        FollowButton.Visibility = Visibility.Collapsed;
        _rows.Clear();
        RowsPanel.Children.Clear();
        if (document.IsSynced)
        {
            for (var index = 0; index < document.Lines.Count; index++)
            {
                var row = BuildRow(document.Lines[index], index);
                _rows.Add(row);
                RowsPanel.Children.Add(row.Button);
            }
        }
        EmptyMessage.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Text = document.IsInstrumental ? "纯音乐，请欣赏" : string.IsNullOrWhiteSpace(document.PlainText) ? "播放后自动获取同步歌词" : document.PlainText;
        ScrollTo(0, false);
        ScheduleLayoutFollow();
    }

    public void UpdatePosition(long positionMs, double offsetMs = 0)
    {
        _clock.Update(positionMs, false);
        UpdatePositionCore(positionMs, offsetMs);
    }

    public void UpdatePlayback(long positionMs, bool playing, double offsetMs = 0, bool forceRebase = false)
    {
        _clock.Update(positionMs, playing, forceRebase);
        UpdatePositionCore((long)_clock.PositionMs, offsetMs);
        if (IsLoaded && IsVisible) StartRendering();
    }

    public double InterpolatedPositionMs => _clock.PositionMs;

    private void UpdatePositionCore(long positionMs, double offsetMs)
    {
        if (_hasPosition && positionMs != _positionMs) _lastMotionAt = Stopwatch.GetTimestamp();
        _positionMs = positionMs;
        _offsetMs = double.IsFinite(offsetMs) ? offsetMs : 0;
        _hasPosition = true;
        if (_manualScrolling && ManualWaitElapsed() && PlaybackIsAdvancing() && !IsScrollBarInput(Mouse.Captured as DependencyObject)) ResumeFollow();
        var index = _document.IsSynced ? LyricsService.FindLineIndex(_document, positionMs, _offsetMs) : -1;
        if (index == _activeIndex)
        {
            if (index >= 0 && index < _rows.Count) _rows[index].Main.UpdatePlayback(positionMs + _offsetMs);
            return;
        }
        var previous = _activeIndex;
        _activeIndex = index;
        // Beyond five neighbours the focus scale is constant. A normal line change
        // touches only the old and new neighbourhoods, regardless of song length.
        if (previous < 0 || index < 0)
            for (var rowIndex = 0; rowIndex < _rows.Count; rowIndex++) ApplyRowFocus(rowIndex);
        else
        {
            for (var rowIndex = Math.Max(0, previous - 5); rowIndex <= Math.Min(_rows.Count - 1, previous + 5); rowIndex++) ApplyRowFocus(rowIndex);
            for (var rowIndex = Math.Max(0, index - 5); rowIndex <= Math.Min(_rows.Count - 1, index + 5); rowIndex++)
                if (Math.Abs(rowIndex - previous) > 5) ApplyRowFocus(rowIndex);
        }
        if (index >= 0 && index < _rows.Count) _rows[index].Main.UpdatePlayback(positionMs + _offsetMs);
        if (!_manualScrolling && IsVisible) CenterActiveLine(true);
        ScheduleKaraokePreparation();
    }

    private void ApplyRowFocus(int index) => ApplyFocus(_rows[index], index == _activeIndex, CanAnimate(_rows[index]));

    public void ApplyAppearance(LyricAppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        _appearance = appearance.Clone();
        _appearance.Normalize();
        _currentColor = ParseColor(_appearance.CurrentColor, Color.FromRgb(0xBF, 0xD7, 0xFF));
        _inactiveColor = ParseColor(_appearance.InactiveColor, Color.FromRgb(0x82, 0x96, 0xB2));
        _translationColor = ParseColor(_appearance.TranslationColor, Color.FromRgb(0xC2, 0xCD, 0xDF));
        if (!_appearance.AnimationsEnabled) StopScroll();
        for (var index = 0; index < _rows.Count; index++)
        {
            StyleRow(_rows[index]);
            ApplyFocus(_rows[index], index == _activeIndex, false);
        }
        EmptyMessage.FontFamily = new FontFamily(_appearance.FontFamily);
        EmptyMessage.FontSize = Math.Clamp(_appearance.MainFontSize * .72, 14, 26);
        EmptyMessage.Foreground = new SolidColorBrush(_inactiveColor);
        ScheduleLayoutFollow();
    }

    private LineVisual BuildRow(LyricsLine line, int index)
    {
        var parts = line.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var main = new OutlinedLyricsText { TextWrapping = TextWrapping.Wrap };
        main.SetLine(line, string.IsNullOrWhiteSpace(parts[0]) ? "♪" : parts[0]);
        var translation = new TextBlock
        {
            Text = string.Join('\n', parts.Skip(1)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0),
            Visibility = parts.Length > 1 && parts.Skip(1).Any(x => !string.IsNullOrWhiteSpace(x)) ? Visibility.Visible : Visibility.Collapsed,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        // Each line is a small reusable surface. Scrolling and focus scaling move
        // these surfaces instead of retessellating every outlined character.
        var content = new StackPanel { CacheMode = new BitmapCache(1.5) };
        content.Children.Add(main); content.Children.Add(translation);
        var scale = new ScaleTransform(1, 1);
        var slide = new TranslateTransform();
        var transforms = new TransformGroup(); transforms.Children.Add(scale); transforms.Children.Add(slide);
        content.RenderTransform = transforms;
        var button = new Button { Content = content, Style = (Style)FindResource("LyricLineButton"), Tag = index, ToolTip = "点击跳转至 " + FormatTime(line.Time) };
        AutomationProperties.SetAutomationId(button, $"LyricLine_{index}");
        AutomationProperties.SetName(button, $"{FormatTime(line.Time)} · {line.Text.Replace('\n', ' ')}");
        AutomationProperties.SetHelpText(button, "点击或按回车跳转到这一句歌词");
        button.Click += (_, _) => ActivateLine(index);
        button.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { ActivateLine(index); e.Handled = true; } };
        button.GotKeyboardFocus += (_, _) =>
        {
            if (!IsVisible) return;
            SuspendFollow();
            CenterLine(index, true);
        };
        // Keyboard navigation gets the same smooth centering as playback, never a native jump.
        button.RequestBringIntoView += (_, e) => e.Handled = true;
        var row = new LineVisual(index, button, content, main, translation, scale, slide);
        StyleRow(row);
        ApplyFocus(row, false, false);
        return row;
    }

    private void StyleRow(LineVisual row)
    {
        var alignment = _appearance.Alignment switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center };
        row.Main.FontFamily = row.Translation.FontFamily = new FontFamily(_appearance.FontFamily);
        row.Main.ApplyLyricsStyle(_appearance);
        row.Main.FontSize = _appearance.MainFontSize;
        row.Main.FontWeight = FontWeight.FromOpenTypeWeight(_appearance.MainFontWeight);
        row.Main.LineHeight = _appearance.MainFontSize * 1.48;
        row.Translation.FontSize = Math.Max(11, _appearance.MainFontSize * _appearance.SecondaryScale);
        row.Translation.FontWeight = FontWeights.Normal;
        row.Translation.LineHeight = row.Translation.FontSize * 1.58;
        row.Main.TextAlignment = alignment;
        row.Translation.TextAlignment = alignment;
        row.Content.RenderTransformOrigin = new Point(alignment == TextAlignment.Left ? 0 : alignment == TextAlignment.Right ? 1 : .5, .5);
    }

    private void ApplyFocus(LineVisual row, bool active, bool animate)
    {
        row.Main.KaraokeEnabled = active;
        var duration = TimeSpan.FromMilliseconds(_appearance.TransitionDurationMs);
        var color = active ? row.Main.Words.Count > 0 ? ParseColor(_appearance.UnsungColor, _currentColor) : _currentColor : _inactiveColor;
        AnimateColor(row.MainBrush, color, duration, animate);
        AnimateColor(row.TranslationBrush, _translationColor, duration, animate);
        AnimateDouble(row.Main, OpacityProperty, active ? 1 : .72, duration, animate);
        AnimateDouble(row.Translation, OpacityProperty, active ? .9 : .55, duration, animate);
        var scale = _activeIndex < 0 ? 1 : LyricScrollMath.FocusScale(row.Index - _activeIndex, _appearance.MainFocusScale);
        AnimateDouble(row.Scale, ScaleTransform.ScaleXProperty, scale, duration, animate);
        AnimateDouble(row.Scale, ScaleTransform.ScaleYProperty, scale, duration, animate);
        row.Slide.BeginAnimation(TranslateTransform.YProperty, null);
        row.Slide.Y = 0;
        if (animate && active && _appearance.LyricTransition == "Slide")
            row.Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(9, 0, duration) { EasingFunction = Ease(), FillBehavior = FillBehavior.Stop });
        AutomationProperties.SetItemStatus(row.Button, active ? "当前歌词" : "歌词");
    }

    private bool CanAnimate(LineVisual row)
    {
        if (!IsVisible || !_appearance.AnimationsEnabled || _appearance.LyricTransition == "None" || !row.Button.IsArrangeValid) return false;
        return LyricScrollMath.IsInViewport(RowTop(row), row.Button.ActualHeight, CurrentScrollOffset, Scroller.ViewportHeight);
    }

    private void ScheduleLayoutFollow(bool animate = false)
    {
        if (!IsLoaded || !IsVisible) return;
        _animateAfterLayout |= animate;
        if (_layoutScheduled) return;
        _layoutScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            try
            {
                if (!IsLoaded || !IsVisible) return;
                var viewport = Math.Max(0, Scroller.ViewportHeight);
                TopSpace.Height = BottomSpace.Height = _rows.Count > 0 ? viewport * .5 : 0;
                // Leave room for the render-only scale without clipping wrapped text at either edge.
                var side = Math.Max(20, ActualWidth * Math.Max(0, _appearance.MainFocusScale - 1) * .5 + 16);
                RowsPanel.Margin = new Thickness(side, 0, side, 0);
                // Keep the guard until layout is finished: extent/viewport notifications here are
                // already included in the measurements below and must not enqueue a second snap.
                Scroller.UpdateLayout();
                if (!_manualScrolling) CenterActiveLine(_animateAfterLayout, preserveMotion: true);
                ScheduleKaraokePreparation();
            }
            finally { _layoutScheduled = false; _animateAfterLayout = false; }
        });
    }

    private void ScheduleKaraokePreparation()
    {
        if (!IsLoaded || !IsVisible) return;
        var next = Math.Max(0, _activeIndex + 1);
        if (next >= _rows.Count || _rows[next].Main.Words.Count == 0) return;
        var version = ++_karaokePreparationVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (version != _karaokePreparationVersion || !IsVisible || next >= _rows.Count) return;
            var text = _rows[next].Main;
            if (text.IsArrangeValid && text.ActualWidth > 0) text.PrepareKaraokeLayout();
        });
    }

    private void CenterActiveLine(bool animate, bool preserveMotion = false) => CenterLine(_activeIndex < 0 ? 0 : _activeIndex, animate, preserveMotion);

    private void CenterLine(int index, bool animate, bool preserveMotion = false)
    {
        if (!IsVisible || index < 0 || index >= _rows.Count || Scroller.ViewportHeight <= 0) return;
        var row = _rows[index];
        if (!row.Button.IsArrangeValid) { ScheduleLayoutFollow(animate); return; }
        var target = LyricScrollMath.CenterOffset(RowTop(row), row.Button.ActualHeight, Scroller.ViewportHeight, Scroller.ExtentHeight);
        ScrollTo(target, animate, preserveMotion: preserveMotion);
    }

    private double RowTop(LineVisual row) => row.Button.TranslatePoint(new Point(), LineHost).Y;

    private void ScrollTo(double target, bool animate, double? durationMs = null, bool preserveMotion = false)
    {
        target = Math.Clamp(double.IsFinite(target) ? target : 0, 0, Math.Max(0, Scroller.ScrollableHeight));
        var current = CurrentScrollOffset;
        if (preserveMotion && _scrollMoving && _appearance.AnimationsEnabled && IsVisible)
        {
            // A late remeasure may change the destination, but it cannot discard the motion.
            // An unchanged destination keeps the original easing clock entirely intact.
            if (Math.Abs(target - _targetScrollOffset) < .5) { _targetScrollOffset = target; return; }
            var remaining = _scrollDuration - _scrollTime.GetElapsedTime(_scrollStarted).TotalMilliseconds;
            if (remaining > 0 && Math.Abs(target - current) >= .5)
            {
                _scrollFrom = current;
                _targetScrollOffset = target;
                _scrollDuration = remaining;
                _scrollStarted = _scrollTime.GetTimestamp();
                return;
            }
        }
        _scrollMoving = false;
        _targetScrollOffset = target;
        if (!animate || !_appearance.AnimationsEnabled || !IsVisible || Math.Abs(target - current) < .5)
        {
            CommitScroll(target);
            return;
        }
        _scrollFrom = current;
        _scrollDuration = durationMs ?? _appearance.ScrollDurationMs;
        _scrollStarted = _scrollTime.GetTimestamp();
        _scrollMoving = true;
        StartRendering();
    }

    private void StopScroll()
    {
        _scrollMoving = false;
        var current = CurrentScrollOffset;
        CommitScroll(current);
        _targetScrollOffset = current;
    }

    private void SetVisualScroll(double offset)
    {
        _visualOffset = offset;
        _ownsVisualOffset = true;
        ScrollTranslation.Y = Scroller.VerticalOffset - offset;
    }

    private void CommitScroll(double offset)
    {
        SetVisualScroll(offset);
        if (Math.Abs(Scroller.VerticalOffset - offset) < .1)
        {
            _ownsVisualOffset = false;
            ScrollTranslation.Y = 0;
        }
        else Scroller.ScrollToVerticalOffset(offset);
    }

    private void SuspendFollow()
    {
        if (_rows.Count == 0) return;
        _manualScrolling = true;
        _manualInputAt = Stopwatch.GetTimestamp();
        StopScroll();
        FollowButton.Visibility = Visibility.Visible;
        _followTimer.Stop();
        if (IsVisible && IsLoaded) _followTimer.Start();
    }

    private void ResumeFollow()
    {
        _manualScrolling = false;
        _followTimer.Stop();
        FollowButton.Visibility = Visibility.Collapsed;
        CenterActiveLine(true);
    }

    private bool ManualWaitElapsed() => _manualInputAt != 0 && Stopwatch.GetElapsedTime(_manualInputAt) >= TimeSpan.FromSeconds(5);
    private bool PlaybackIsAdvancing() => _clock.IsPlaying || (_lastMotionAt != 0 && Stopwatch.GetElapsedTime(_lastMotionAt) < TimeSpan.FromSeconds(1.2));
    private void FollowTimer_Tick(object? sender, EventArgs args)
    {
        _followTimer.Stop();
        if (_manualScrolling && ManualWaitElapsed() && PlaybackIsAdvancing() && !IsScrollBarInput(Mouse.Captured as DependencyObject)) ResumeFollow();
        // Paused playback keeps the user's reading position; UpdatePosition resumes following
        // when audio starts advancing again, or the explicit button does so immediately.
    }

    private void FollowButton_Click(object sender, RoutedEventArgs args) => ResumeFollow();

    private void ScrollWheel(object sender, MouseWheelEventArgs args)
    {
        var previousTarget = _manualScrolling ? _targetScrollOffset : CurrentScrollOffset;
        SuspendFollow();
        var wheelLines = SystemParameters.WheelScrollLines;
        var step = wheelLines < 0 ? Scroller.ViewportHeight * .82 : Math.Max(1, wheelLines) * 25;
        ScrollTo(previousTarget - args.Delta / 120d * step, true, Math.Min(240, _appearance.ScrollDurationMs));
        args.Handled = true;
    }

    private void ScrollKey(object sender, KeyEventArgs args)
    {
        var target = args.Key switch
        {
            Key.PageUp => CurrentScrollOffset - Scroller.ViewportHeight * .8,
            Key.PageDown => CurrentScrollOffset + Scroller.ViewportHeight * .8,
            Key.Home => 0,
            Key.End => Scroller.ScrollableHeight,
            _ => double.NaN
        };
        if (!double.IsFinite(target)) return;
        SuspendFollow();
        ScrollTo(target, true, Math.Min(300, _appearance.ScrollDurationMs));
        args.Handled = true;
    }

    private void ActivateLine(int index)
    {
        if (index < 0 || index >= _document.Lines.Count) return;
        if (_lastActivationIndex == index && _lastActivationAt != 0 && Stopwatch.GetElapsedTime(_lastActivationAt) < TimeSpan.FromMilliseconds(350)) return;
        _lastActivationIndex = index; _lastActivationAt = Stopwatch.GetTimestamp();
        _manualScrolling = false;
        _followTimer.Stop();
        FollowButton.Visibility = Visibility.Collapsed;
        SeekRequested?.Invoke((long)_document.Lines[index].Time.TotalMilliseconds);
    }

    private void StopMotion()
    {
        _karaokePreparationVersion++;
        _animateAfterLayout = false;
        _followTimer.Stop();
        StopScroll();
        for (var index = 0; index < _rows.Count; index++) ApplyFocus(_rows[index], index == _activeIndex, false);
    }

    private void StartRendering()
    {
        if (_renderAttached || !IsLoaded || !IsVisible) return;
        CompositionTarget.Rendering += RenderFrame;
        _renderAttached = true;
    }
    private void StopRendering()
    {
        if (!_renderAttached) return;
        CompositionTarget.Rendering -= RenderFrame;
        _renderAttached = false;
    }
    private void RenderFrame(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        if (_clock.IsPlaying) UpdatePositionCore((long)_clock.PositionMs, _offsetMs);
        if (!_scrollMoving) return;
        var progress = _scrollTime.GetElapsedTime(_scrollStarted).TotalMilliseconds / _scrollDuration;
        var value = _scrollFrom + (_targetScrollOffset - _scrollFrom) * LyricScrollMath.EaseOut(progress);
        SetVisualScroll(value);
        if (progress >= 1) { _scrollMoving = false; CommitScroll(_targetScrollOffset); }
    }

    private static bool IsScrollBarInput(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ScrollBar or Thumb) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static CubicEase Ease() => new() { EasingMode = EasingMode.EaseOut };
    private static string FormatTime(TimeSpan value) => value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    private static Color ParseColor(string text, Color fallback) { try { return (Color)ColorConverter.ConvertFromString(text); } catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException) { return fallback; } }

    private static void AnimateColor(SolidColorBrush brush, Color target, TimeSpan duration, bool animate)
    {
        var current = brush.Color;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        brush.Color = target;
        if (animate && current != target) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(current, target, duration) { EasingFunction = Ease(), FillBehavior = FillBehavior.Stop });
    }

    private static void AnimateDouble(DependencyObject target, DependencyProperty property, double value, TimeSpan duration, bool animate)
    {
        var current = (double)target.GetValue(property);
        var animation = animate && Math.Abs(current - value) > .0001 ? new DoubleAnimation(current, value, duration) { EasingFunction = Ease(), FillBehavior = FillBehavior.Stop } : null;
        if (target is Animatable animatable) { animatable.BeginAnimation(property, null); target.SetValue(property, value); if (animation is not null) animatable.BeginAnimation(property, animation); }
        else if (target is UIElement element) { element.BeginAnimation(property, null); target.SetValue(property, value); if (animation is not null) element.BeginAnimation(property, animation); }
        else target.SetValue(property, value);
    }

    private sealed class LineVisual
    {
        public int Index { get; }
        public Button Button { get; }
        public StackPanel Content { get; }
        public OutlinedLyricsText Main { get; }
        public TextBlock Translation { get; }
        public ScaleTransform Scale { get; }
        public TranslateTransform Slide { get; }
        public SolidColorBrush MainBrush { get; } = new();
        public SolidColorBrush TranslationBrush { get; } = new();
        public LineVisual(int index, Button button, StackPanel content, OutlinedLyricsText main, TextBlock translation, ScaleTransform scale, TranslateTransform slide)
        {
            Index = index; Button = button; Content = content; Main = main; Translation = translation; Scale = scale; Slide = slide;
            Main.Foreground = MainBrush; Translation.Foreground = TranslationBrush;
        }
    }
}
