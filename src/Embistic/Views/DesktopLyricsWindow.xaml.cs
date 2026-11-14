using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Embistic.Models;
using Embistic.Services;
using Embistic.Controls;

namespace Embistic.Views;

public partial class DesktopLyricsWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExNoActivate = 0x08000000;
    private static readonly CubicEase TransitionEase = CreateEase();
    private bool _isLocked;
    private bool _applyingAppearance;
    private double _offsetMs;
    private LyricsDocument? _document;
    private LyricsDocument? _renderedDocument;
    private int _renderedIndex = int.MinValue;
    private long _positionMs;
    private string _title = "";
    private string _artist = "";
    private string _displayedCurrent = "让音乐流动";
    private string _displayedSecondary = "播放歌曲后，将自动查找歌词";
    private string _unfilteredSecondary = "播放歌曲后，将自动查找歌词";
    private bool _secondaryIsTranslation;
    private int _transitionVersion;
    private LyricAppearanceSettings _appearance = new();
    private readonly LyricPlaybackClock _clock = new();
    private bool _renderAttached;
    private readonly DispatcherTimer _hoverTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(33) };
    private bool _hovered, _dragging, _resizing, _contextMenuOpen;
    private Brush _hoverBackground = Brushes.Transparent;
    private static readonly Brush HoverBorder = Brush("#BFD7FF", .68);

    public event Action? UserClosed;
    public event Action? OptionsChanged;

    public DesktopLyricsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeStyle();
        _hoverTimer.Tick += (_, _) => UpdatePointerHover();
        Surface.ContextMenu.Opened += (_, _) => { _contextMenuOpen = true; UpdatePointerHover(); };
        Surface.ContextMenu.Closed += (_, _) => { _contextMenuOpen = false; UpdatePointerHover(); };
        SizeChanged += Window_SizeChanged;
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) { StopTransition(); StopRendering(); } else StartRendering(); UpdateHoverTracking(); };
        Loaded += (_, _) => { StartRendering(); UpdateHoverTracking(); };
        Closed += (_, _) => { StopRendering(); _hoverTimer.Stop(); };
        ApplyAppearance(_appearance);
    }

    public double OffsetMs
    {
        get => _offsetMs;
        set
        {
            var offset = double.IsFinite(value) ? Math.Clamp(value, -600000, 600000) : 0;
            if (Math.Abs(offset - _offsetMs) < .01) return;
            _offsetMs = offset;
            RefreshLyrics();
            UpdateModeLabel();
            OptionsChanged?.Invoke();
        }
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            if (_isLocked == value) return;
            _isLocked = value;
            if (value) Surface.ContextMenu.IsOpen = false;
            ApplyNativeStyle();
            UpdateHoverTracking();
            UpdateModeLabel();
            OptionsChanged?.Invoke();
        }
    }

    public double LyricsFontSize
    {
        get => _appearance.DesktopFontSize;
        set
        {
            var size = double.IsFinite(value) ? Math.Clamp(value, 16, 96) : 32;
            if (Math.Abs(size - _appearance.DesktopFontSize) < .01) return;
            var appearance = _appearance.Clone();
            appearance.DesktopFontSize = size;
            ApplyAppearance(appearance);
            OptionsChanged?.Invoke();
        }
    }

    /// <summary>Applies a detached, validated snapshot without triggering settings-save events.</summary>
    public void ApplyAppearance(LyricAppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        _applyingAppearance = true;
        try
        {
            _appearance = appearance.Clone();
            _appearance.Normalize();
            StopTransition();
            FontFamily = new FontFamily(_appearance.FontFamily);
            Topmost = _appearance.DesktopTopmost;
            Width = _appearance.DesktopWidth;
            SizeToContent = SizeToContent.Height;
            var alignment = Align(_appearance.DesktopMainAlignment);
            var mainBrush = Brush(_appearance.SungColor);
            var effect = _appearance.ShadowEnabled ? new DropShadowEffect
            {
                Color = ParseColor(_appearance.ShadowColor), Opacity = _appearance.ShadowOpacity,
                BlurRadius = _appearance.ShadowBlur, ShadowDepth = _appearance.ShadowDepth, Direction = 270
            } : null;
            effect?.Freeze();
            foreach (var line in new[] { CurrentLine, PreviousCurrentLine })
            {
                line.FontSize = _appearance.DesktopFontSize;
                line.FontWeight = FontWeight.FromOpenTypeWeight(_appearance.DesktopFontWeight);
                line.Foreground = mainBrush;
                line.TextAlignment = alignment;
                line.Effect = effect;
                line.ApplyLyricsStyle(_appearance);
            }
            foreach (var line in new[] { NextLine, PreviousNextLine })
            {
                line.FontSize = _appearance.DesktopFontSize * _appearance.SecondaryScale;
                line.FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(_appearance.DesktopFontWeight - 200, 100, 900));
                line.TextAlignment = Align(_appearance.DesktopSecondaryAlignment);
                line.Effect = effect;
                line.ApplyLyricsStyle(_appearance);
            }
            var backgroundColor = ParseColor(_appearance.BackgroundColor);
            var hoverOpacity = Math.Clamp(_appearance.BackgroundOpacity * backgroundColor.A / 255d, .2, 1);
            backgroundColor.A = 255;
            var background = new SolidColorBrush(backgroundColor) { Opacity = hoverOpacity };
            background.Freeze(); _hoverBackground = background;
            UpdateBorder();
            RefreshLyrics(force: true, animate: false);
            if (_document is null) SetDisplayedLyrics(_displayedCurrent, _unfilteredSecondary, _secondaryIsTranslation, animate: false, force: true);
        }
        finally { _applyingAppearance = false; }
    }

    public void SetTrack(string title, string artist)
    {
        _title = title;
        _artist = artist;
        TrackLabel.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title}  ·  {artist}";
        _document = null;
        _renderedDocument = null;
        _renderedIndex = int.MinValue;
        _clock.Update(0, false);
        SetDisplayedLyrics(title, "正在查找歌词…", false);
    }

    public void UpdateLyrics(LyricsDocument document, long positionMs, bool playing = false, bool forceRebase = false)
    {
        _document = document;
        _positionMs = positionMs;
        _clock.Update(positionMs, playing, forceRebase);
        _positionMs = (long)_clock.PositionMs;
        RefreshLyrics();
        UpdateWordProgress();
        StartRendering();
    }

    public void UpdatePlayback(long positionMs, bool playing, bool forceRebase = false)
    {
        _positionMs = positionMs;
        _clock.Update(positionMs, playing, forceRebase);
        _positionMs = (long)_clock.PositionMs;
        RefreshLyrics(); UpdateWordProgress(); StartRendering();
    }

    public double InterpolatedPositionMs => _clock.PositionMs;

    private void RefreshLyrics(bool force = false, bool animate = true)
    {
        if (_document is null) return;
        var index = _document.IsSynced ? LyricsService.FindLineIndex(_document, _positionMs, _offsetMs) : -2;
        // Playback ticks perform only lookup and identity checks; no string/layout/animation allocations.
        if (!force && ReferenceEquals(_renderedDocument, _document) && _renderedIndex == index) return;
        _renderedDocument = _document;
        _renderedIndex = index;
        if (!_document.IsSynced || _document.Lines.Count == 0)
        {
            var primary = string.IsNullOrWhiteSpace(_title) ? "Embistic" : _title;
            var secondary = _document.IsInstrumental ? "纯音乐，请欣赏" : !string.IsNullOrWhiteSpace(_document.Source)
                ? "此歌曲只有文本歌词，请在主窗口查看"
                : string.IsNullOrWhiteSpace(_document.PlainText) ? _artist : _document.PlainText;
            SetDisplayedLyrics(primary, secondary, false, animate, force);
            return;
        }
        if (index < 0)
        {
            SetDisplayedLyrics(_title, FirstLine(_document.Lines[0].Text), false, animate, force);
            return;
        }
        var text = _document.Lines[index].Text;
        var split = text.IndexOf('\n');
        var primaryText = split >= 0 ? text[..split] : text;
        var hasTranslation = _appearance.DesktopLayout == "Translation" && split >= 0 && split + 1 < text.Length;
        var secondaryText = hasTranslation ? text[(split + 1)..] : index + 1 < _document.Lines.Count ? FirstLine(_document.Lines[index + 1].Text) : "";
        SetDisplayedLyrics(string.IsNullOrWhiteSpace(primaryText) ? "♪" : primaryText, secondaryText, hasTranslation, animate, force);
        var line = _document.Lines[index];
        CurrentLine.SetLine(line, string.IsNullOrWhiteSpace(primaryText) ? "♪" : primaryText);
        CurrentLine.Foreground = Brush(line.Words.Count > 0 ? _appearance.UnsungColor : _appearance.SungColor);
        if (hasTranslation) NextLine.SetLine(line, secondaryText, split + 1);
        else NextLine.SetLine(index + 1 < _document.Lines.Count ? _document.Lines[index + 1] : null, secondaryText);
        if (NextLine.Words.Count > 0) NextLine.Foreground = Brush(_appearance.UnsungColor);
        UpdateWordProgress();
    }

    private void SetDisplayedLyrics(string current, string secondary, bool isTranslation, bool animate = true, bool force = false)
    {
        _unfilteredSecondary = secondary;
        var showSecondary = _appearance.DesktopLayout != "Single" && !string.IsNullOrWhiteSpace(secondary);
        var displayedSecondary = showSecondary ? secondary : "";
        var changed = current != _displayedCurrent || displayedSecondary != _displayedSecondary || isTranslation != _secondaryIsTranslation;
        if (!changed && !force) return;
        var useAnimation = changed && animate && IsVisible && _appearance.AnimationsEnabled && _appearance.LyricTransition != "None";
        // On a rapid seek, retain whichever layer is currently most visible.
        var retainOutgoing = OutgoingCanvas.Visibility == Visibility.Visible && OutgoingLayer.Opacity > CurrentLayer.Opacity;
        var previousOpacity = retainOutgoing ? OutgoingLayer.Opacity : CurrentLayer.Opacity;
        var previousTranslation = retainOutgoing ? OutgoingTranslate.Y : CurrentTranslate.Y;
        var previousCurrent = retainOutgoing ? PreviousCurrentLine.Text : _displayedCurrent;
        var previousSecondary = retainOutgoing ? PreviousNextLine.Text : _displayedSecondary;
        var previousSecondaryBrush = retainOutgoing ? PreviousNextLine.Foreground : NextLine.Foreground;
        StopTransition();
        if (useAnimation)
        {
            PreviousCurrentLine.Text = previousCurrent;
            PreviousNextLine.Text = previousSecondary;
            PreviousNextLine.Visibility = string.IsNullOrEmpty(previousSecondary) ? Visibility.Collapsed : Visibility.Visible;
            PreviousNextLine.Foreground = previousSecondaryBrush;
            OutgoingCanvas.Visibility = Visibility.Visible;
        }
        _displayedCurrent = current;
        _displayedSecondary = displayedSecondary;
        _secondaryIsTranslation = isTranslation;
        CurrentLine.SetLine(null, current);
        NextLine.SetLine(null, displayedSecondary);
        NextLine.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
        NextLine.Foreground = Brush(isTranslation ? _appearance.TranslationColor : _appearance.InactiveColor);
        SizeToContent = SizeToContent.Height;
        if (useAnimation) StartTransition(previousOpacity, previousTranslation);
    }

    private void StartTransition(double previousOpacity, double previousTranslation)
    {
        var version = _transitionVersion;
        var duration = TimeSpan.FromMilliseconds(_appearance.TransitionDurationMs);
        var fadeOut = Animation(previousOpacity, 0, duration);
        fadeOut.Completed += (_, _) => { if (version == _transitionVersion) OutgoingCanvas.Visibility = Visibility.Collapsed; };
        CurrentLayer.BeginAnimation(OpacityProperty, Animation(0, 1, duration), HandoffBehavior.SnapshotAndReplace);
        OutgoingLayer.BeginAnimation(OpacityProperty, fadeOut, HandoffBehavior.SnapshotAndReplace);
        if (_appearance.LyricTransition == "Slide")
        {
            var distance = Math.Min(18, _appearance.DesktopFontSize * .35);
            CurrentTranslate.BeginAnimation(TranslateTransform.YProperty, Animation(distance, 0, duration), HandoffBehavior.SnapshotAndReplace);
            OutgoingTranslate.BeginAnimation(TranslateTransform.YProperty, Animation(previousTranslation, -distance, duration), HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void StopTransition()
    {
        _transitionVersion++;
        CurrentLayer.BeginAnimation(OpacityProperty, null);
        OutgoingLayer.BeginAnimation(OpacityProperty, null);
        CurrentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        OutgoingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        CurrentLayer.Opacity = 1;
        OutgoingLayer.Opacity = 0;
        CurrentTranslate.Y = 0;
        OutgoingTranslate.Y = 0;
        OutgoingCanvas.Visibility = Visibility.Collapsed;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_applyingAppearance || !e.WidthChanged || Math.Abs(e.NewSize.Width - _appearance.DesktopWidth) < .5) return;
        _appearance.DesktopWidth = Math.Clamp(e.NewSize.Width, 400, 2200);
        // Manual horizontal resizing must keep long lyric lines wrapped and fully visible.
        if (SizeToContent != SizeToContent.Height) SizeToContent = SizeToContent.Height;
        if (!_resizing) OptionsChanged?.Invoke();
    }

    private void UpdateBorder()
    {
        // Decorations never participate in the text's layout. In particular the
        // toolbar's fixed row does not resize a SizeToContent window on hover.
        var visible = _hovered && !_isLocked && IsVisible;
        Surface.Background = visible ? _hoverBackground : Brushes.Transparent;
        HoverOutline.BorderBrush = visible ? HoverBorder : Brushes.Transparent;
        HoverControls.Visibility = WidthGrip.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
        TrackHeader.Visibility = !_appearance.ShowTrackInfo ? Visibility.Collapsed : visible ? Visibility.Visible : Visibility.Hidden;
    }

    private void UpdateHoverTracking()
    {
        _hoverTimer.Stop();
        if (IsVisible && IsLoaded && !_isLocked) _hoverTimer.Start();
        UpdatePointerHover();
    }

    private void UpdatePointerHover()
    {
        var inside = false;
        // Zero-alpha pixels in a layered HWND are skipped before WPF receives a
        // MouseEnter or WM_NCHITTEST. Probe the physical client rectangle instead
        // of drawing an almost-transparent rectangle over the desktop.
        if (IsVisible && !_isLocked)
        {
            var handle = new WindowInteropHelper(this).Handle;
            inside = _dragging || _resizing || _contextMenuOpen || handle != IntPtr.Zero &&
                GetCursorPos(out var pointer) && GetWindowRect(handle, out var bounds) &&
                pointer.X >= bounds.Left && pointer.X < bounds.Right && pointer.Y >= bounds.Top && pointer.Y < bounds.Bottom;
        }
        if (_hovered == inside) return;
        _hovered = inside;
        UpdateBorder();
    }

    private void UpdateWordProgress()
    {
        CurrentLine.UpdatePlayback(_positionMs + _offsetMs);
        NextLine.UpdatePlayback(_positionMs + _offsetMs);
    }
    private void StartRendering()
    {
        if (_renderAttached || !IsLoaded || !IsVisible) return;
        CompositionTarget.Rendering += RenderFrame; _renderAttached = true;
    }
    private void StopRendering()
    {
        if (!_renderAttached) return;
        CompositionTarget.Rendering -= RenderFrame; _renderAttached = false;
    }
    private void RenderFrame(object? sender, EventArgs args)
    {
        if (!_clock.IsPlaying) return;
        _positionMs = (long)_clock.PositionMs;
        RefreshLyrics(); UpdateWordProgress();
    }
    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        UpdatePointerHover();
    }
    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        UpdatePointerHover();
    }
    private void UpdateModeLabel()
    {
        var mode = _isLocked ? "已锁定 · 主窗口可解锁" : "拖动移动 · 右键设置";
        ModeLabel.Text = Math.Abs(_offsetMs) < .1 ? mode : $"{mode} · 偏移 {_offsetMs / 1000:+0.00;-0.00;0} s";
    }

    private void ApplyNativeStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64() | WsExToolWindow | WsExNoActivate;
        style = _isLocked ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        // A click through a locked overlay may raise another topmost window.
        // Explicitly unlocking restores this editor's position in its own band,
        // without taking keyboard focus or changing its rectangle.
        if (!_isLocked && IsVisible) SetWindowPos(handle, Topmost ? new IntPtr(-1) : IntPtr.Zero, 0, 0, 0, 0, 0x0013);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked || e.ChangedButton != MouseButton.Left || IsControlInput(e.OriginalSource as DependencyObject)) return;
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _dragging = false; UpdatePointerHover(); }
        OptionsChanged?.Invoke();
    }

    private static bool IsControlInput(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or Thumb) return true;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return false;
    }
    private void WidthGrip_DragStarted(object sender, DragStartedEventArgs e) { _resizing = true; UpdatePointerHover(); e.Handled = true; }
    private void WidthGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isLocked) Width = Math.Clamp(Width + e.HorizontalChange, 400, 2200);
        e.Handled = true;
    }
    private void WidthGrip_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _resizing = false; UpdatePointerHover(); OptionsChanged?.Invoke(); e.Handled = true;
    }

    private void Lock_Click(object sender, RoutedEventArgs e) => IsLocked = true;
    private void Advance_Click(object sender, RoutedEventArgs e) => OffsetMs += 250;
    private void Delay_Click(object sender, RoutedEventArgs e) => OffsetMs -= 250;
    private void ResetOffset_Click(object sender, RoutedEventArgs e) => OffsetMs = 0;
    private void Larger_Click(object sender, RoutedEventArgs e) => LyricsFontSize += 2;
    private void Smaller_Click(object sender, RoutedEventArgs e) => LyricsFontSize -= 2;
    private void Hide_Click(object sender, RoutedEventArgs e) { Hide(); UserClosed?.Invoke(); }

    protected override void OnClosing(CancelEventArgs e)
    {
        StopTransition();
        if (Application.Current?.MainWindow is { IsVisible: true })
        {
            e.Cancel = true;
            Hide();
            UserClosed?.Invoke();
        }
        base.OnClosing(e);
    }

    private static string FirstLine(string text) { var end = text.IndexOf('\n'); return end >= 0 ? text[..end] : text; }
    private static TextAlignment Align(string value) => value switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center };
    private static Color ParseColor(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static SolidColorBrush Brush(string value, double opacity = 1)
    {
        var brush = new SolidColorBrush(ParseColor(value)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }
    private static CubicEase CreateEase() { var ease = new CubicEase { EasingMode = EasingMode.EaseOut }; ease.Freeze(); return ease; }
    private static DoubleAnimation Animation(double from, double to, TimeSpan duration) => new(from, to, duration) { EasingFunction = TransitionEase, FillBehavior = FillBehavior.Stop };

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
