using System.Text.RegularExpressions;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Embistic.Models;

namespace Embistic.Controls;

/// <summary>Shared inline editor. Input is coalesced; validated changes immediately update the live lyric view.</summary>
public partial class LyricAppearanceEditor : UserControl
{
    private static readonly Regex HexColor = new(@"^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant);
    private static readonly Lazy<string[]> FontNames = new(() => Fonts.SystemFontFamilies.Select(f => f.Source)
        .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToArray());
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
    private LyricAppearanceSettings _editable;
    private bool _loading;
    private string _lastPublished = "";
    public LyricAppearanceSettings Result { get; private set; }
    public bool IsInputValid { get; private set; } = true;
    public event Action<LyricAppearanceSettings>? AppearanceChanged;
    public event Action<string>? CoverModeChanged;
    public event Action<double>? OffsetChanged;
    public event Action? OffsetResetRequested;
    public event Action? ImportRequested;
    public event Action<bool>? DesktopChanged;

    public LyricAppearanceEditor(LyricAppearanceSettings initial)
    {
        _editable = initial.Clone(); _editable.Normalize(); Result = _editable.Clone();
        _loading = true;
        InitializeComponent();
        FontFamilyCombo.ItemsSource = FontNames.Value;
        var weights = Enumerable.Range(1, 9).Select(i => new WeightChoice(i * 100, i * 100 + (i == 4 ? " · 常规" : i == 6 ? " · 半粗" : i == 7 ? " · 粗体" : ""))).ToArray();
        MainWeightCombo.ItemsSource = DesktopWeightCombo.ItemsSource = weights;
        LayoutCombo.ItemsSource = new[] { new Choice("Translation", "译文双行"), new Choice("NextLine", "当前句 + 下一句"), new Choice("Single", "只显示当前句") };
        AlignmentCombo.ItemsSource = new[] { new Choice("Left", "居左"), new Choice("Center", "居中"), new Choice("Right", "居右") };
        DesktopMainAlignmentCombo.ItemsSource = DesktopSecondaryAlignmentCombo.ItemsSource = AlignmentCombo.ItemsSource;
        SungOutlineStyleCombo.ItemsSource = UnsungOutlineStyleCombo.ItemsSource = new[] { new Choice("Round", "圆角描边"), new Choice("Bevel", "斜角描边"), new Choice("Miter", "尖角描边") };
        TransitionCombo.ItemsSource = new[] { new Choice("Slide", "轻滑与淡入"), new Choice("Fade", "柔和淡入"), new Choice("None", "直接切换") };
        CoverModeBox.ItemsSource = new[] { new Choice("Circle", "圆形专辑封面"), new Choice("Vinyl", "唱片旋转封面"), new Choice("RoundedSquare", "圆角方形封面") };
        DataContext = _editable;
        _loading = false;
        EditorRoot.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, e) => { if (IsAppearanceInput(e.OriginalSource)) QueueRefresh(); }));
        EditorRoot.AddHandler(RangeBase.ValueChangedEvent, new RoutedPropertyChangedEventHandler<double>((_, e) => { if (e.OriginalSource is Slider && IsAppearanceInput(e.OriginalSource)) QueueRefresh(); }));
        EditorRoot.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, e) => { if (e.OriginalSource is ComboBox && IsAppearanceInput(e.OriginalSource)) QueueRefresh(); }));
        EditorRoot.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is CheckBox && IsAppearanceInput(e.OriginalSource)) QueueRefresh(); }));
        EditorRoot.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((_, e) => { if (e.OriginalSource is CheckBox && IsAppearanceInput(e.OriginalSource)) QueueRefresh(); }));
        _refresh.Tick += (_, _) => Flush();
        Unloaded += (_, _) => Flush();
        Refresh(false);
    }

    public void SetPlaybackOptions(string coverMode, double offsetMs, bool desktopEnabled)
    {
        _loading = true;
        CoverModeBox.SelectedValue = coverMode;
        OffsetValue.Text = $"{offsetMs:+0;-0;0} ms";
        DesktopToggle.IsChecked = desktopEnabled;
        _loading = false;
    }
    public void SetSourcesEditor(UIElement editor) => SourcesHost.Content = editor;
    public void SelectSources() => SettingsTabs.SelectedItem = SourcesTab;
    public void SelectAppearance() => SettingsTabs.SelectedIndex = 0;
    public void Flush() { if (!_refresh.IsEnabled) return; _refresh.Stop(); Refresh(true); }
    private void QueueRefresh() { if (!_loading && !_refresh.IsEnabled) _refresh.Start(); }
    private bool IsAppearanceInput(object source) => source is FrameworkElement element
        && ReferenceEquals(element.DataContext, _editable) && !SourcesHost.IsAncestorOf(element)
        && !ReferenceEquals(element, CoverModeBox) && !ReferenceEquals(element, DesktopToggle);

    private void Refresh(bool notify)
    {
        var candidate = _editable.Clone();
        IsInputValid = false;
        var colors = new[] { candidate.CurrentColor, candidate.InactiveColor, candidate.TranslationColor, candidate.BackgroundColor,
            candidate.ShadowColor, candidate.SungColor, candidate.UnsungColor, candidate.SungOutlineColor, candidate.UnsungOutlineColor };
        if (colors.Any(c => c is null || !HexColor.IsMatch(c.Trim()))) { ValidationMessage.Text = "颜色请输入 #RGB、#RRGGBB 或 #AARRGGBB"; return; }
        var font = candidate.FontFamily?.Trim();
        if (string.IsNullOrWhiteSpace(font) || font.Length > 256 || font.Any(char.IsControl) || font.IndexOfAny([':', '/', '\\', '#', '{', '}', ';']) >= 0)
        { ValidationMessage.Text = "请输入字体名称，多个字体以英文逗号分隔"; return; }
        candidate.Normalize(); Result = candidate; IsInputValid = true;
        ValidationMessage.Text = "修改即时生效并自动保存";
        var snapshot = JsonSerializer.Serialize(candidate);
        if (_lastPublished == snapshot) return;
        _lastPublished = snapshot;
        CurrentColorChip.Background = Brush(candidate.CurrentColor); InactiveColorChip.Background = Brush(candidate.InactiveColor);
        TranslationColorChip.Background = Brush(candidate.TranslationColor); BackgroundColorChip.Background = Brush(candidate.BackgroundColor); ShadowColorChip.Background = Brush(candidate.ShadowColor);
        if (notify) AppearanceChanged?.Invoke(candidate.Clone());
    }
    private void CoverMode_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading && CoverModeBox.SelectedValue is string mode) CoverModeChanged?.Invoke(mode); }
    private void Earlier_Click(object sender, RoutedEventArgs e) => OffsetChanged?.Invoke(-250);
    private void Later_Click(object sender, RoutedEventArgs e) => OffsetChanged?.Invoke(250);
    private void ResetOffset_Click(object sender, RoutedEventArgs e) => OffsetResetRequested?.Invoke();
    private void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();
    private void Desktop_Click(object sender, RoutedEventArgs e) { if (!_loading) DesktopChanged?.Invoke(DesktopToggle.IsChecked == true); }
    private void Palette_Click(object sender, RoutedEventArgs e)
    {
        var colors = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "White" => new[] { "#F0F3F8", "#8796AC", "#C7D0DF" }, "Sand" => new[] { "#F1CCA0", "#AD937A", "#DFCFB9" },
            "Mint" => new[] { "#B4E7D1", "#84AB9D", "#C4DDD4" }, _ => new[] { "#BFD7FF", "#8296B2", "#C2CDDF" }
        };
        _editable.CurrentColor = _editable.SungColor = colors[0]; _editable.InactiveColor = colors[1]; _editable.TranslationColor = colors[2];
        Rebind(); Refresh(true);
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { _editable = new(); Rebind(); Refresh(true); }
    private void Rebind() { _loading = true; DataContext = null; DataContext = _editable; _loading = false; }
    private static SolidColorBrush Brush(string value) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; }
    private sealed record Choice(string Value, string Label);
    private sealed record WeightChoice(int Value, string Label);
}
