using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Embistic.Models;

namespace Embistic.Controls;

/// <summary>One measured text layout, two independently outlined paints, clipped using actual timed text ranges.</summary>
public sealed class OutlinedLyricsText : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(OutlinedLyricsText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(nameof(TextAlignment), typeof(TextAlignment), typeof(OutlinedLyricsText), new FrameworkPropertyMetadata(TextAlignment.Center, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextWrappingProperty = DependencyProperty.Register(nameof(TextWrapping), typeof(TextWrapping), typeof(OutlinedLyricsText), new FrameworkPropertyMetadata(TextWrapping.Wrap, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LineHeightProperty = DependencyProperty.Register(nameof(LineHeight), typeof(double), typeof(OutlinedLyricsText), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }
    public TextWrapping TextWrapping { get => (TextWrapping)GetValue(TextWrappingProperty); set => SetValue(TextWrappingProperty, value); }
    public double LineHeight { get => (double)GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    public IReadOnlyList<LyricsWord> Words { get; private set; } = Array.Empty<LyricsWord>();
    public Brush SungBrush { get; private set; } = Brushes.LightBlue;
    public Brush SungOutlineBrush { get; private set; } = Brushes.Black;
    public Brush UnsungOutlineBrush { get; private set; } = Brushes.Black;
    public double SungOutlineWidth { get; private set; }
    public double UnsungOutlineWidth { get; private set; }
    public double PlaybackPositionMs { get; private set; }
    private bool _karaokeEnabled = true;
    public bool KaraokeEnabled
    {
        get => _karaokeEnabled;
        set { if (_karaokeEnabled == value) return; _karaokeEnabled = value; InvalidateVisual(); }
    }
    private PenLineJoin _sungJoin = PenLineJoin.Round, _unsungJoin = PenLineJoin.Round;
    private FormattedText? _layout;
    private Geometry? _geometry;
    private string _layoutKey = "";
    private readonly List<(LyricsWord Word, Rect Bounds, int Index, int Count)> _glyphs = [];

    public OutlinedLyricsText() { Focusable = false; IsHitTestVisible = false; }

    public void SetLine(LyricsLine? line, string text, int textOffset = 0)
    {
        Text = text;
        Words = line?.Words.Where(w => w.TextIndex >= textOffset && w.TextIndex < textOffset + text.Length)
            .Select(w => w with { TextIndex = w.TextIndex - textOffset, Text = w.Text[..Math.Min(w.Text.Length, text.Length - (w.TextIndex - textOffset))] }).ToArray() ?? Array.Empty<LyricsWord>();
        _layoutKey = "";
        InvalidateVisual();
    }

    public void ApplyLyricsStyle(LyricAppearanceSettings appearance)
    {
        SungBrush = Brush(appearance.SungColor);
        SungOutlineBrush = Brush(appearance.SungOutlineColor);
        UnsungOutlineBrush = Brush(appearance.UnsungOutlineColor);
        SungOutlineWidth = appearance.SungOutlineWidth;
        UnsungOutlineWidth = appearance.UnsungOutlineWidth;
        _sungJoin = Join(appearance.SungOutlineStyle);
        _unsungJoin = Join(appearance.UnsungOutlineStyle);
        InvalidateMeasure(); InvalidateVisual();
    }

    public void UpdatePlayback(double positionMs)
    {
        if (PlaybackPositionMs == positionMs) return;
        PlaybackPositionMs = positionMs;
        if (Words.Count > 0) InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        EnsureLayout(constraint.Width);
        var padding = Math.Max(SungOutlineWidth, UnsungOutlineWidth) + 2;
        return new Size(Math.Min(double.IsFinite(constraint.Width) ? constraint.Width : double.MaxValue, (_layout?.WidthIncludingTrailingWhitespace ?? 0) + padding * 2), (_layout?.Height ?? 0) + padding * 2);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        EnsureLayout(ActualWidth);
        if (_geometry is null) return;
        DrawText(drawing, Foreground, UnsungOutlineBrush, UnsungOutlineWidth, _unsungJoin);
        if (Words.Count == 0 || !KaraokeEnabled) return;
        var clip = new GeometryGroup();
        var margin = Math.Max(SungOutlineWidth, UnsungOutlineWidth) + 1;
        foreach (var glyph in _glyphs)
        {
            var duration = (glyph.Word.End - glyph.Word.Start).TotalMilliseconds;
            var progress = duration > 0 ? Math.Clamp((PlaybackPositionMs - glyph.Word.Start.TotalMilliseconds) / duration, 0, 1) : PlaybackPositionMs >= glyph.Word.Start.TotalMilliseconds ? 1 : 0;
            var fraction = Math.Clamp(progress * glyph.Count - glyph.Index, 0, 1);
            if (fraction <= 0) continue;
            var bounds = glyph.Bounds;
            bounds.Inflate(margin, margin);
            bounds.Width = fraction >= 1 ? bounds.Width : Math.Max(0, glyph.Bounds.Width * fraction + margin);
            clip.Children.Add(new RectangleGeometry(bounds));
        }
        if (clip.Children.Count == 0) return;
        drawing.PushClip(clip);
        DrawText(drawing, SungBrush, SungOutlineBrush, SungOutlineWidth, _sungJoin);
        drawing.Pop();
    }

    private void DrawText(DrawingContext drawing, Brush fill, Brush outline, double width, PenLineJoin join)
    {
        // Paint the outline underneath the fill so fine Chinese/Latin strokes retain their full ink width.
        if (width > 0) drawing.DrawGeometry(null, Outline(outline, width, join), _geometry);
        drawing.DrawGeometry(fill, null, _geometry);
    }

    private void EnsureLayout(double width)
    {
        width = TextWrapping == TextWrapping.Wrap && double.IsFinite(width) ? Math.Max(1, width) : 100_000;
        var padding = Math.Max(SungOutlineWidth, UnsungOutlineWidth) + 2;
        var contentWidth = Math.Max(1, width - padding * 2);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = $"{Text}|{contentWidth}|{FontFamily.Source}|{FontSize}|{FontWeight}|{TextAlignment}|{LineHeight}|{dpi}";
        if (_layoutKey == key && _layout is not null) return;
        _layoutKey = key;
        _layout = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.White, dpi)
        { MaxTextWidth = contentWidth, TextAlignment = TextAlignment };
        if (double.IsFinite(LineHeight) && LineHeight > 0) _layout.LineHeight = LineHeight;
        var origin = new Point(padding, padding);
        _geometry = _layout.BuildGeometry(origin);
        _geometry.Freeze();
        _glyphs.Clear();
        foreach (var word in Words)
        {
            var indices = StringInfo.ParseCombiningCharacters(word.Text);
            for (var i = 0; i < indices.Length; i++)
            {
                var start = word.TextIndex + indices[i];
                var length = (i + 1 < indices.Length ? indices[i + 1] : word.Text.Length) - indices[i];
                if (start < 0 || start + length > (Text?.Length ?? 0)) continue;
                var highlight = _layout.BuildHighlightGeometry(origin, start, length);
                if (highlight is not null) _glyphs.Add((word, highlight.Bounds, i, indices.Length));
            }
        }
    }
    private static PenLineJoin Join(string style) => style switch { "Bevel" => PenLineJoin.Bevel, "Miter" => PenLineJoin.Miter, _ => PenLineJoin.Round };
    private static Pen? Outline(Brush brush, double width, PenLineJoin join) => width > 0 ? new Pen(brush, width * 2) { LineJoin = join } : null;
    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}
