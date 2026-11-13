using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Interop;
using System.Windows.Threading;
using Embistic.Models;

namespace Embistic.Views;

/// <summary>An independent native editor; callers own persistence and cancel rollback.</summary>
public partial class LyricAppearanceWindow : Window
{
    private static readonly Regex HexColor = new(@"^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant);
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    private LyricAppearanceSettings _editable;
    private bool _initialized, _loading, _refreshQueued, _closed, _alternate;

    public event Action<LyricAppearanceSettings>? PreviewChanged;
    public LyricAppearanceSettings Result { get; private set; }

    public LyricAppearanceWindow(LyricAppearanceSettings initial)
    {
        _editable = initial?.Clone() ?? new LyricAppearanceSettings();
        _editable.Normalize();
        Result = _editable.Clone();
        InitializeComponent();

        FontFamilyCombo.ItemsSource = Fonts.SystemFontFamilies.Select(family => family.Source)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var weights = new[]
        {
            new WeightChoice(100, "100 · 极细"), new WeightChoice(200, "200 · 纤细"), new WeightChoice(300, "300 · 细体"),
            new WeightChoice(400, "400 · 常规"), new WeightChoice(500, "500 · 中等"), new WeightChoice(600, "600 · 半粗"),
            new WeightChoice(700, "700 · 粗体"), new WeightChoice(800, "800 · 特粗"), new WeightChoice(900, "900 · 极粗")
        };
        MainWeightCombo.ItemsSource = weights;
        DesktopWeightCombo.ItemsSource = weights;
        LayoutCombo.ItemsSource = new[] { new Choice("Translation", "译文双行"), new Choice("NextLine", "当前句 + 下一句"), new Choice("Single", "只显示当前句") };
        AlignmentCombo.ItemsSource = new[] { new Choice("Left", "居左"), new Choice("Center", "居中"), new Choice("Right", "居右") };
        TransitionCombo.ItemsSource = new[] { new Choice("Slide", "轻滑与淡入"), new Choice("Fade", "柔和淡入"), new Choice("None", "直接切换") };
        foreach (var chip in new[] { CurrentColorChip, InactiveColorChip, TranslationColorChip, BackgroundColorChip, ShadowColorChip })
        {
            chip.BorderBrush = Brush("#4D617B");
            chip.BorderThickness = new Thickness(1);
        }
        BindEditor();

        EditorRoot.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => QueueRefresh()));
        EditorRoot.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, e) =>
        {
            if (e.OriginalSource is Slider) QueueRefresh();
        }));
        EditorRoot.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is ComboBox) QueueRefresh();
        }));
        EditorRoot.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is CheckBox) QueueRefresh(); }));
        EditorRoot.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is CheckBox) QueueRefresh(); }));
        _previewTimer.Tick += PreviewTimer_Tick;
        _initialized = true;
        ApplyPreview(Result);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        var caption = 0x0015100C;
        var text = 0x00FAF2ED;
        _ = DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private void BindEditor()
    {
        _loading = true;
        DataContext = null;
        DataContext = _editable;
        _loading = false;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 40));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 40));
        RefreshPreview(false);
    }

    private void QueueRefresh()
    {
        if (!_initialized || _loading || _closed || _refreshQueued) return;
        _refreshQueued = true;
        // Let two-way bindings finish before inspecting the editable values.
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _refreshQueued = false;
            if (!_closed && !_loading) RefreshPreview(true);
        }));
    }

    private bool TryGetCandidate(out LyricAppearanceSettings candidate, out string error)
    {
        candidate = _editable.Clone();
        var colors = new (string Label, string? Value)[]
        {
            ("当前歌词颜色", candidate.CurrentColor), ("未播放歌词颜色", candidate.InactiveColor),
            ("翻译歌词颜色", candidate.TranslationColor), ("背景颜色", candidate.BackgroundColor), ("阴影颜色", candidate.ShadowColor)
        };
        foreach (var (label, value) in colors)
        {
            if (value is null || !HexColor.IsMatch(value.Trim()))
            {
                error = $"{label}请输入 #RRGGBB 或 #AARRGGBB。";
                return false;
            }
        }
        var font = candidate.FontFamily?.Trim();
        if (string.IsNullOrWhiteSpace(font) || font.Length > 256 || font.Any(char.IsControl) || font.IndexOfAny([':', '/', '\\', '#', '{', '}', ';']) >= 0)
        {
            error = "请输入字体名称；多个字体请用英文逗号分隔。";
            return false;
        }
        if (FindChildren<FrameworkElement>(EditorRoot).Any(Validation.GetHasError))
        {
            error = "请完成当前输入后再保存。";
            return false;
        }
        candidate.Normalize();
        error = "";
        return true;
    }

    private bool RefreshPreview(bool notify)
    {
        if (!TryGetCandidate(out var candidate, out var error))
        {
            SaveButton.IsEnabled = false;
            ValidationMessage.Text = error;
            ValidationMessage.Foreground = Brush("#F3BC9B");
            return false;
        }
        SaveButton.IsEnabled = true;
        ValidationMessage.Text = "更改会实时预览，保存后保留。";
        ValidationMessage.Foreground = Brush("#93A3B8");
        Result = candidate.Clone();
        ApplyPreview(candidate);
        if (notify) PreviewChanged?.Invoke(candidate.Clone());
        return true;
    }

    private void ApplyPreview(LyricAppearanceSettings settings)
    {
        var family = new FontFamily(settings.FontFamily);
        var alignment = settings.Alignment switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center };
        var primary = _alternate ? "沿着节拍，走向下一段风景" : SampleMainBox.Text;
        var translation = _alternate ? "A new melody is waiting ahead." : SampleTranslationBox.Text;
        var next = _alternate ? "留下这一刻，和安静的回响" : "沿着节拍，走向下一段风景";

        MainPreviousPreview.Text = _alternate ? SampleMainBox.Text : "耳边的旋律，慢慢靠近";
        MainCurrentPreview.Text = primary;
        MainTranslationPreview.Text = translation;
        MainNextPreview.Text = next;
        foreach (var line in new[] { MainPreviousPreview, MainCurrentPreview, MainTranslationPreview, MainNextPreview })
        {
            line.FontFamily = family;
            line.TextAlignment = alignment;
        }
        MainCurrentPreview.FontSize = settings.MainFontSize;
        MainCurrentPreview.FontWeight = FontWeight.FromOpenTypeWeight(settings.MainFontWeight);
        MainCurrentPreview.Foreground = Brush(settings.CurrentColor);
        MainPreviousPreview.Foreground = MainNextPreview.Foreground = Brush(settings.InactiveColor);
        MainTranslationPreview.Foreground = Brush(settings.TranslationColor);
        MainTranslationPreview.FontSize = Math.Max(11, settings.MainFontSize * settings.SecondaryScale);
        MainPreviousPreview.FontSize = MainNextPreview.FontSize = Math.Max(13, settings.MainFontSize * .7);

        DesktopPreviewSurface.Width = settings.DesktopWidth;
        DesktopPreviewSurface.Background = Brush(settings.BackgroundColor, settings.BackgroundOpacity);
        DesktopTrackPreview.Visibility = settings.ShowTrackInfo ? Visibility.Visible : Visibility.Collapsed;
        DesktopTrackPreview.TextAlignment = alignment;
        DesktopCurrentPreview.Text = primary;
        DesktopSecondaryPreview.Text = settings.DesktopLayout == "NextLine" || string.IsNullOrWhiteSpace(translation) ? next : translation;
        DesktopSecondaryPreview.Visibility = settings.DesktopLayout == "Single" ? Visibility.Collapsed : Visibility.Visible;
        foreach (var line in new[] { DesktopCurrentPreview, DesktopSecondaryPreview })
        {
            line.FontFamily = family;
            line.FontWeight = FontWeight.FromOpenTypeWeight(settings.DesktopFontWeight);
            line.TextAlignment = alignment;
            line.Effect = CreateShadow(settings);
        }
        DesktopCurrentPreview.FontSize = settings.DesktopFontSize;
        DesktopSecondaryPreview.FontSize = settings.DesktopFontSize * settings.SecondaryScale;
        DesktopCurrentPreview.Foreground = Brush(settings.CurrentColor);
        DesktopSecondaryPreview.Foreground = Brush(settings.DesktopLayout == "NextLine" ? settings.InactiveColor : settings.TranslationColor);
        var alignmentLabel = settings.Alignment switch { "Left" => "居左", "Right" => "居右", _ => "居中" };
        var layoutLabel = settings.DesktopLayout switch { "Single" => "单行", "NextLine" => "当前句 + 下一句", _ => "译文双行" };
        DesktopPreviewCaption.Text = $"{settings.DesktopWidth:0} px · {alignmentLabel} · {layoutLabel}" + (settings.DesktopTopmost ? " · 置顶" : "");
        CurrentColorChip.Background = Brush(settings.CurrentColor);
        InactiveColorChip.Background = Brush(settings.InactiveColor);
        TranslationColorChip.Background = Brush(settings.TranslationColor);
        BackgroundColorChip.Background = Brush(settings.BackgroundColor);
        ShadowColorChip.Background = Brush(settings.ShadowColor);
        StopPreviewAnimations(settings);
        UpdatePreviewTimer(settings);
    }

    private static DropShadowEffect? CreateShadow(LyricAppearanceSettings settings)
    {
        if (!settings.ShadowEnabled) return null;
        var color = (Color)ColorConverter.ConvertFromString(settings.ShadowColor);
        return new DropShadowEffect
        {
            Color = Color.FromRgb(color.R, color.G, color.B), BlurRadius = settings.ShadowBlur,
            ShadowDepth = settings.ShadowDepth, Opacity = settings.ShadowOpacity * color.A / 255.0,
            RenderingBias = RenderingBias.Performance
        };
    }

    private void StopPreviewAnimations(LyricAppearanceSettings settings)
    {
        foreach (var line in new[] { MainCurrentPreview, DesktopCurrentPreview, DesktopSecondaryPreview })
        {
            line.BeginAnimation(OpacityProperty, null);
            line.Opacity = 1;
            line.RenderTransform = new TranslateTransform();
        }
        MainCurrentPreview.RenderTransform = new TransformGroup
        {
            Children = new TransformCollection { new ScaleTransform(settings.MainFocusScale, settings.MainFocusScale), new TranslateTransform() }
        };
        MainPreviewSurface.BeginAnimation(OpacityProperty, null);
        MainPreviewSurface.Opacity = 1;
    }

    private void AnimatePreview_Click(object sender, RoutedEventArgs e) => AnimatePreview();
    private void PreviewTimer_Tick(object? sender, EventArgs e) => AnimatePreview();

    private void AnimatePreview()
    {
        if (_closed || !TryGetCandidate(out var settings, out _)) return;
        _alternate = !_alternate;
        ApplyPreview(settings);
        if (!settings.AnimationsEnabled || settings.LyricTransition == "None") return;
        var duration = TimeSpan.FromMilliseconds(settings.TransitionDurationMs);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var line in new[] { MainCurrentPreview, DesktopCurrentPreview, DesktopSecondaryPreview })
        {
            line.BeginAnimation(OpacityProperty, new DoubleAnimation(.05, 1, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            if (settings.LyricTransition == "Slide")
            {
                var translate = line.RenderTransform is TransformGroup group ? group.Children.OfType<TranslateTransform>().First() : (TranslateTransform)line.RenderTransform;
                translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, line == MainCurrentPreview ? TimeSpan.FromMilliseconds(settings.ScrollDurationMs) : duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
            }
        }
        if (MainCurrentPreview.RenderTransform is TransformGroup transforms)
        {
            var scale = transforms.Children.OfType<ScaleTransform>().First();
            var focus = new DoubleAnimation(1, settings.MainFocusScale, TimeSpan.FromMilliseconds(settings.ScrollDurationMs)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, focus);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, focus.Clone());
        }
    }

    private void UpdatePreviewTimer(LyricAppearanceSettings settings)
    {
        if (!_closed && IsVisible && LoopPreviewBox.IsChecked == true && settings.AnimationsEnabled)
        {
            _previewTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(2400, settings.TransitionDurationMs + settings.ScrollDurationMs + 800));
            _previewTimer.Start();
        }
        else _previewTimer.Stop();
    }

    private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || e.OriginalSource != SettingsTabs || !Result.AnimationsEnabled || !Result.PageTransitionsEnabled) return;
        MainPreviewSurface.BeginAnimation(OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(Result.TransitionDurationMs)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
    }

    private void Palette_Click(object sender, RoutedEventArgs e)
    {
        var palette = (sender as FrameworkElement)?.Tag as string;
        var colors = palette switch
        {
            "White" => new[] { "#F0F3F8", "#8796AC", "#C7D0DF" },
            "Sand" => new[] { "#F1CCA0", "#AD937A", "#DFCFB9" },
            "Mint" => new[] { "#B4E7D1", "#84AB9D", "#C4DDD4" },
            _ => new[] { "#BFD7FF", "#8296B2", "#C2CDDF" }
        };
        _editable.CurrentColor = colors[0]; _editable.InactiveColor = colors[1]; _editable.TranslationColor = colors[2];
        BindEditor();
        RefreshPreview(true);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _editable = new LyricAppearanceSettings();
        BindEditor();
        RefreshPreview(true);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        FontFamilyCombo.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
        foreach (var input in FindChildren<TextBox>(EditorRoot)) input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (RefreshPreview(true)) DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _previewTimer.Stop();
        _previewTimer.Tick -= PreviewTimer_Tick;
    }

    private static SolidColorBrush Brush(string color, double opacity = 1)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindChildren<T>(child)) yield return descendant;
        }
    }

    private sealed record WeightChoice(int Value, string Label) { public override string ToString() => Label; }
    private sealed record Choice(string Value, string Label) { public override string ToString() => Label; }
}
