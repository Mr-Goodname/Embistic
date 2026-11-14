using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Embistic.Models;
namespace Embistic.Controls;
public partial class SpectrumSettingsEditor : UserControl
{
    private SpectrumSettings _editable;
    private readonly Dictionary<string, TextBox> _colors = new();
    private readonly DispatcherTimer _refresh = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
    private bool _loading;
    public SpectrumSettings Result { get; private set; }
    public bool IsInputValid { get; private set; } = true;
    public event Action<SpectrumSettings>? PreviewChanged;
    public SpectrumSettingsEditor(SpectrumSettings initial)
    {
        _editable = initial.Clone(); _editable.Normalize(); Result = _editable.Clone(); InitializeComponent(); BuildFields();
        _refresh.Tick += (_, _) => Flush(); Unloaded += (_, _) => Flush(); RefreshPreview(false);
    }
    private void BuildFields()
    {
        _loading = true;
        GeneralFields.Children.Clear(); StyleFields.Children.Clear(); _colors.Clear();
        Heading(GeneralFields, "形态与响应");
        Toggle(GeneralFields, "显示音频响应频谱", _editable.Enabled, value => _editable.Enabled = value);
        var mode = new ComboBox { ItemsSource = new[] { new Choice("Bars", "竖向频段"), new Choice("Mirror", "上下镜像"), new Choice("Wave", "频谱波面"), new Choice("Line", "频率曲线") }, SelectedValuePath = "Value", DisplayMemberPath = "Label", SelectedValue = _editable.Mode, Margin = new Thickness(0, 7, 0, 18) };
        AutomationProperties.SetName(mode, "频谱形态");
        mode.SelectionChanged += (_, _) => { if (mode.SelectedValue is string value) { _editable.Mode = value; Changed(); } };
        GeneralFields.Children.Add(mode);
        Number(GeneralFields, "频段数量", 16, 192, _editable.BarCount, value => _editable.BarCount = (int)value, 1, "SpectrumBarCount");
        Number(GeneralFields, "显示高度", 30, 240, _editable.Height, value => _editable.Height = value, 1, "SpectrumHeight", " px");
        Number(GeneralFields, "响应灵敏度", .1, 8, _editable.Gain, value => _editable.Gain = value, .1, "SpectrumGain", " ×");
        Number(GeneralFields, "响应平滑", 0, .95, _editable.Smoothing, value => _editable.Smoothing = value, .01, "SpectrumSmoothing");
        Number(GeneralFields, "最低频率", 10, 4000, _editable.MinFrequency, value => _editable.MinFrequency = value, 10, "SpectrumMinFrequency", " Hz");
        Number(GeneralFields, "最高频率", 100, 24000, _editable.MaxFrequency, value => _editable.MaxFrequency = value, 100, "SpectrumMaxFrequency", " Hz");
        Number(GeneralFields, "绘制帧率", 30, 144, _editable.FrameRate, value => _editable.FrameRate = (int)value, 1, "SpectrumFrameRate", " fps");
        Hint(GeneralFields, "低平滑值反应更快；高平滑值更柔和。曲线始终圆滑，绘制帧率独立于响应平滑，最高跟随屏幕刷新率。");
        Heading(GeneralFields, "显示位置 · 可多选");
        Toggle(GeneralFields, "顶部", _editable.ShowTop, value => _editable.ShowTop = value, "SpectrumShowTop");
        Toggle(GeneralFields, "底部", _editable.ShowBottom, value => _editable.ShowBottom = value, "SpectrumShowBottom");
        Toggle(GeneralFields, "左侧", _editable.ShowLeft, value => _editable.ShowLeft = value, "SpectrumShowLeft");
        Toggle(GeneralFields, "右侧", _editable.ShowRight, value => _editable.ShowRight = value, "SpectrumShowRight");
        Toggle(GeneralFields, "环绕专辑封面", _editable.ShowRing, value => _editable.ShowRing = value, "SpectrumShowRing");

        Heading(StyleFields, "配色与细节");
        ColorField(StyleFields, "起始颜色", "ColorStart", _editable.ColorStart);
        ColorField(StyleFields, "结束颜色", "ColorEnd", _editable.ColorEnd);
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        foreach (var preset in new[] { ("冰蓝", "#ADC9F0", "#E2F0FF"), ("麦金", "#EDCF86", "#F4A45C"), ("薄荷", "#8FE5CE", "#92BAF0"), ("暮紫", "#B79CFA", "#F0A9BC") })
        {
            var button = new Button { Content = preset.Item1, Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(11, 7, 11, 7) };
            button.Click += (_, _) => { _colors["ColorStart"].Text = preset.Item2; _colors["ColorEnd"].Text = preset.Item3; };
            presets.Children.Add(button);
        }
        StyleFields.Children.Add(presets);
        Number(StyleFields, "透明度", 0, 1, _editable.Opacity, value => _editable.Opacity = value, .01, "SpectrumOpacity");
        Number(StyleFields, "柱宽比例", .15, 1, _editable.BarWidthRatio, value => _editable.BarWidthRatio = value, .01, "SpectrumBarWidth");
        Toggle(StyleFields, "圆角柱体", _editable.RoundedBars, value => _editable.RoundedBars = value);
        Toggle(StyleFields, "峰值保持", _editable.PeakHold, value => _editable.PeakHold = value);
        Toggle(StyleFields, "柔光效果", _editable.GlowEnabled, value => _editable.GlowEnabled = value);
        Number(StyleFields, "柔光范围", 0, 32, _editable.GlowRadius, value => _editable.GlowRadius = value, 1, "SpectrumGlow");
        Hint(StyleFields, "颜色支持 #RRGGBB 与 #AARRGGBB。柱宽、圆角和峰值保持作用于柱形与镜像模式。");
        _loading = false;
    }

    private static void Heading(Panel parent, string text)
    {
        parent.Children.Add(new TextBlock { Text = text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 13) });
    }

    private static void Hint(Panel parent, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 20, Margin = new Thickness(0, 12, 0, 18) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        parent.Children.Add(block);
    }

    private void Toggle(Panel parent, string label, bool initial, Action<bool> assign, string? id = null)
    {
        var checkbox = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(0, 8, 0, 10) };
        AutomationProperties.SetName(checkbox, label);
        if (id is not null) AutomationProperties.SetAutomationId(checkbox, id);
        checkbox.Checked += (_, _) => { assign(true); Changed(); };
        checkbox.Unchecked += (_, _) => { assign(false); Changed(); };
        parent.Children.Add(checkbox);
    }

    private void Number(Panel parent, string label, double minimum, double maximum, double initial, Action<double> assign, double step, string id, string suffix = "")
    {
        var grid = new Grid { Margin = new Thickness(0, 11, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label });
        var valueText = new TextBlock { Text = Value(initial, step) + suffix };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        Grid.SetColumn(valueText, 1); grid.Children.Add(valueText);
        parent.Children.Add(grid);
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = initial, TickFrequency = step, IsSnapToTickEnabled = false, Margin = new Thickness(0, 0, 0, 9) };
        AutomationProperties.SetName(slider, label); AutomationProperties.SetAutomationId(slider, id);
        slider.ValueChanged += (_, e) => { assign(e.NewValue); valueText.Text = Value(e.NewValue, step) + suffix; Changed(); };
        parent.Children.Add(slider);
    }

    private static string Value(double value, double step) => value.ToString(step >= 1 ? "0" : step >= .1 ? "0.0" : "0.00", CultureInfo.CurrentCulture);

    private void ColorField(Panel parent, string label, string property, string initial)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 7) });
        var textbox = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(textbox, label); AutomationProperties.SetAutomationId(textbox, "Spectrum" + property);
        textbox.TextChanged += (_, _) => Changed();
        _colors[property] = textbox; parent.Children.Add(textbox);
    }


    private void Changed() { if (!_loading && !_refresh.IsEnabled) _refresh.Start(); }
    public void Flush() { if (!_refresh.IsEnabled) return; _refresh.Stop(); RefreshPreview(true); }
    private void RefreshPreview(bool notify)
    {
        IsInputValid = false;
        if (_colors.Any(entry => !Regex.IsMatch(entry.Value.Text.Trim(), @"^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")))
        { ValidationText.Text = "请输入完整颜色，例如 #ADC9F0。"; return; }
        if (_editable.MinFrequency >= _editable.MaxFrequency) { ValidationText.Text = "最低频率需要小于最高频率。"; return; }
        if (_editable.Enabled && !_editable.ShowTop && !_editable.ShowBottom && !_editable.ShowLeft && !_editable.ShowRight && !_editable.ShowRing)
        { ValidationText.Text = "请选择显示位置，或关闭频谱。"; return; }
        _editable.ColorStart = _colors["ColorStart"].Text.Trim(); _editable.ColorEnd = _colors["ColorEnd"].Text.Trim();
        Result = _editable.Clone(); Result.Normalize(); IsInputValid = true; ValidationText.Text = "修改即时生效并自动保存";
        if (notify) PreviewChanged?.Invoke(Result.Clone());
    }
    private void Reset_Click(object sender, RoutedEventArgs e) { _editable = new(); BuildFields(); RefreshPreview(true); }
    private sealed record Choice(string Value, string Label) { public override string ToString() => Label; }
}
