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
    private Pen? _sungPen, _unsungPen;
    private FormattedText? _layout;
    private Geometry? _geometry;
    private LayoutKey? _layoutKey;
    private GeometryGroup _karaokeClip = new() { FillRule = FillRule.Nonzero };
    private readonly List<GlyphClip> _glyphs = [];
    private bool _karaokeLayoutReady;
    internal int LayoutBuildCount { get; private set; }
    internal int RenderCount { get; private set; }
    internal int KaraokeLayoutBuildCount { get; private set; }

    public OutlinedLyricsText() { Focusable = false; IsHitTestVisible = false; }

    public void SetLine(LyricsLine? line, string text, int textOffset = 0)
    {
        Text = text;
        Words = line?.Words.Where(w => w.TextIndex >= textOffset && w.TextIndex < textOffset + text.Length)
            .Select(w => w with { TextIndex = w.TextIndex - textOffset, Text = w.Text[..Math.Min(w.Text.Length, text.Length - (w.TextIndex - textOffset))] }).ToArray() ?? Array.Empty<LyricsWord>();
        _layoutKey = null;
        InvalidateVisual();
    }

    public void ApplyLyricsStyle(LyricAppearanceSettings appearance)
    {
        var oldPadding = Math.Max(SungOutlineWidth, UnsungOutlineWidth);
        SungBrush = Brush(appearance.SungColor);
        SungOutlineBrush = Brush(appearance.SungOutlineColor);
        UnsungOutlineBrush = Brush(appearance.UnsungOutlineColor);
        SungOutlineWidth = appearance.SungOutlineWidth;
        UnsungOutlineWidth = appearance.UnsungOutlineWidth;
        _sungJoin = Join(appearance.SungOutlineStyle);
        _unsungJoin = Join(appearance.UnsungOutlineStyle);
        _sungPen = Outline(SungOutlineBrush, SungOutlineWidth, _sungJoin);
        _unsungPen = Outline(UnsungOutlineBrush, UnsungOutlineWidth, _unsungJoin);
        if (Math.Abs(oldPadding - Math.Max(SungOutlineWidth, UnsungOutlineWidth)) > .001) InvalidateMeasure();
        InvalidateVisual();
    }

    public void UpdatePlayback(double positionMs)
    {
        if (PlaybackPositionMs == positionMs) return;
        PlaybackPositionMs = positionMs;
        if (Words.Count > 0 && KaraokeEnabled) UpdateKaraokeClip();
    }

    internal void PrepareKaraokeLayout()
    {
        if (Words.Count == 0 || ActualWidth <= 0) return;
        EnsureLayout(ActualWidth);
        EnsureKaraokeLayout();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        EnsureLayout(constraint.Width);
        var padding = Math.Max(SungOutlineWidth, UnsungOutlineWidth) + 2;
        return new Size(Math.Min(double.IsFinite(constraint.Width) ? constraint.Width : double.MaxValue, (_layout?.WidthIncludingTrailingWhitespace ?? 0) + padding * 2), (_layout?.Height ?? 0) + padding * 2);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        RenderCount++;
        EnsureLayout(ActualWidth);
        if (_geometry is null && _layout is not null)
        {
            var padding = Math.Max(SungOutlineWidth, UnsungOutlineWidth) + 2;
            _geometry = _layout.BuildGeometry(new Point(padding, padding));
            _geometry.Freeze();
        }
        if (_geometry is null) return;
        DrawText(drawing, Foreground, _unsungPen);
        if (Words.Count == 0 || !KaraokeEnabled) return;
        EnsureKaraokeLayout();
        UpdateKaraokeClip();
        // WPF retains this mutable clip in its drawing instructions. Advancing a
        // word changes only its rectangle; the text geometry is never rerecorded.
        drawing.PushClip(_karaokeClip);
        DrawText(drawing, SungBrush, _sungPen);
        drawing.Pop();
    }

    private void UpdateKaraokeClip()
    {
        var margin = Math.Max(SungOutlineWidth, UnsungOutlineWidth) + 1;
        foreach (var glyph in _glyphs)
        {
            var duration = (glyph.Word.End - glyph.Word.Start).TotalMilliseconds;
            var progress = duration > 0 ? Math.Clamp((PlaybackPositionMs - glyph.Word.Start.TotalMilliseconds) / duration, 0, 1) : PlaybackPositionMs >= glyph.Word.Start.TotalMilliseconds ? 1 : 0;
            var fraction = Math.Clamp(progress * glyph.Count - glyph.Index, 0, 1);
            var bounds = Rect.Empty;
            if (fraction > 0)
            {
                bounds = glyph.Bounds;
                bounds.Inflate(margin, margin);
                bounds.Width = fraction >= 1 ? bounds.Width : Math.Max(0, glyph.Bounds.Width * fraction + margin);
            }
            if (glyph.Clip.Rect != bounds) glyph.Clip.Rect = bounds;
        }
    }

    private void DrawText(DrawingContext drawing, Brush fill, Pen? outline)
    {
        // Paint the outline underneath the fill so fine Chinese/Latin strokes retain their full ink width.
        if (outline is not null) drawing.DrawGeometry(null, outline, _geometry);
        drawing.DrawGeometry(fill, null, _geometry);
    }

    private void EnsureLayout(double width)
    {
        width = TextWrapping == TextWrapping.Wrap && double.IsFinite(width) ? Math.Max(1, width) : 100_000;
        var padding = Math.Max(SungOutlineWidth, UnsungOutlineWidth) + 2;
        var contentWidth = Math.Max(1, width - padding * 2);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = new LayoutKey(Text, contentWidth, padding, FontFamily.Source, FontSize, FontWeight, FontStyle, FontStretch, TextAlignment, LineHeight, FlowDirection, dpi);
        if (_layoutKey == key && _layout is not null) return;
        LayoutBuildCount++;
        _layoutKey = key;
        _layout = new FormattedText(Text ?? "", CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Brushes.White, dpi)
        { MaxTextWidth = contentWidth, TextAlignment = TextAlignment };
        if (double.IsFinite(LineHeight) && LineHeight > 0) _layout.LineHeight = LineHeight;
        _geometry = null;
        _glyphs.Clear();
        _karaokeClip = new GeometryGroup { FillRule = FillRule.Nonzero };
        _karaokeLayoutReady = false;
    }

    private void EnsureKaraokeLayout()
    {
        if (_karaokeLayoutReady || _layout is null) return;
        _karaokeLayoutReady = true;
        KaraokeLayoutBuildCount++;
        var padding = Math.Max(SungOutlineWidth, UnsungOutlineWidth) + 2;
        var origin = new Point(padding, padding);
        var characters = BuildCharacterBounds(origin);
        foreach (var word in Words)
        {
            var indices = StringInfo.ParseCombiningCharacters(word.Text);
            for (var i = 0; i < indices.Length; i++)
            {
                var start = word.TextIndex + indices[i];
                var length = (i + 1 < indices.Length ? indices[i + 1] : word.Text.Length) - indices[i];
                if (start < 0 || start + length > (Text?.Length ?? 0)) continue;
                var bounds = Rect.Empty;
                for (var character = start; character < start + length; character++)
                    if (characters.TryGetValue(character, out var characterBounds)) bounds.Union(characterBounds);
                // Complex/bidirectional runs use WPF's exact range geometry. For
                // the usual horizontal CJK/Latin runs the already shaped glyph
                // advances supply every character bound in a single layout pass.
                if (bounds.IsEmpty) bounds = _layout.BuildHighlightGeometry(origin, start, length)?.Bounds ?? Rect.Empty;
                if (!bounds.IsEmpty)
                {
                    var clip = new RectangleGeometry(Rect.Empty);
                    _glyphs.Add(new GlyphClip(word, bounds, i, indices.Length, clip));
                    _karaokeClip.Children.Add(clip);
                }
            }
        }
    }

    private Dictionary<int, Rect> BuildCharacterBounds(Point origin)
    {
        var result = new Dictionary<int, Rect>();
        var drawing = new DrawingGroup();
        using (var context = drawing.Open()) context.DrawText(_layout!, origin);
        var sourcePosition = 0;
        CollectBounds(drawing, result, ref sourcePosition);
        return result;
    }

    private void CollectBounds(Drawing drawing, Dictionary<int, Rect> result, ref int sourcePosition)
    {
        if (drawing is DrawingGroup group)
        {
            foreach (var child in group.Children) CollectBounds(child, result, ref sourcePosition);
            return;
        }
        if (drawing is not GlyphRunDrawing { GlyphRun: { } run } || run.Characters is null || run.Characters.Count == 0) return;
        var text = new string(run.Characters.ToArray());
        var sourceStart = Text.IndexOf(text, sourcePosition, StringComparison.Ordinal);
        if (sourceStart < 0) return;
        sourcePosition = sourceStart + text.Length;
        if ((run.BidiLevel & 1) != 0 || run.AdvanceWidths is null || run.ClusterMap is null || run.ClusterMap.Count != text.Length) return;
        var advances = new double[run.AdvanceWidths.Count + 1];
        for (var glyph = 0; glyph < run.AdvanceWidths.Count; glyph++) advances[glyph + 1] = advances[glyph] + run.AdvanceWidths[glyph];
        var vertical = run.ComputeAlignmentBox();
        for (var character = 0; character < text.Length; character++)
        {
            var first = (int)run.ClusterMap[character];
            var last = advances.Length - 1;
            for (var next = character + 1; next < text.Length; next++)
                if (run.ClusterMap[next] != first) { last = run.ClusterMap[next]; break; }
            if (first < 0 || last < first || last >= advances.Length) continue;
            result[sourceStart + character] = new Rect(run.BaselineOrigin.X + advances[first], run.BaselineOrigin.Y + vertical.Y,
                Math.Max(0, advances[last] - advances[first]), Math.Max(0, vertical.Height));
        }
    }
    private readonly record struct LayoutKey(string Text, double Width, double Padding, string Family, double Size, FontWeight Weight,
        FontStyle Style, FontStretch Stretch, TextAlignment Alignment, double LineHeight, FlowDirection Direction, double Dpi);
    private readonly record struct GlyphClip(LyricsWord Word, Rect Bounds, int Index, int Count, RectangleGeometry Clip);
    private static PenLineJoin Join(string style) => style switch { "Bevel" => PenLineJoin.Bevel, "Miter" => PenLineJoin.Miter, _ => PenLineJoin.Round };
    private static Pen? Outline(Brush brush, double width, PenLineJoin join)
    {
        if (width <= 0) return null;
        var pen = new Pen(brush, width * 2) { LineJoin = join };
        pen.Freeze();
        return pen;
    }
    private static Brush Brush(string color) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); brush.Freeze(); return brush; }
}
