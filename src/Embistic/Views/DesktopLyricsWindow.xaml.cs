using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Embistic.Models;
using Embistic.Services;

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

    public event Action? UserClosed;
    public event Action? OptionsChanged;

    public DesktopLyricsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyNativeStyle();
        SizeChanged += Window_SizeChanged;
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) StopTransition(); };
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
            ResizeMode = value ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
            UpdateBorder();
            ApplyNativeStyle();
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
            TrackHeader.Visibility = _appearance.ShowTrackInfo ? Visibility.Visible : Visibility.Collapsed;
            var alignment = _appearance.Alignment switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center };
            var mainBrush = Brush(_appearance.CurrentColor);
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
            }
            foreach (var line in new[] { NextLine, PreviousNextLine })
            {
                line.FontSize = _appearance.DesktopFontSize * _appearance.SecondaryScale;
                line.FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(_appearance.DesktopFontWeight - 200, 100, 900));
                line.TextAlignment = alignment;
                line.Effect = effect;
            }
            Surface.Background = Brush(_appearance.BackgroundColor, _appearance.BackgroundOpacity);
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
        SetDisplayedLyrics(title, "正在查找歌词…", false);
    }

    public void UpdateLyrics(LyricsDocument document, long positionMs)
    {
        _document = document;
        _positionMs = positionMs;
        RefreshLyrics();
    }

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
        CurrentLine.Text = current;
        NextLine.Text = displayedSecondary;
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
        OptionsChanged?.Invoke();
    }

    private void UpdateBorder() => Surface.BorderBrush = _isLocked ? Brushes.Transparent : Brush("#3D9EBFEF");
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
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isLocked || e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        OptionsChanged?.Invoke();
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
}
