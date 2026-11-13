using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Embistic.Models;
using Embistic.Services;
using NAudio.Wave;

namespace Embistic.Views;

public partial class SpectrumSettingsWindow : Window
{
    private SpectrumSettings _editable;
    private readonly Dictionary<string, TextBox> _colors = new();
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private readonly AudioSpectrumAnalyzer _previewAnalyzer = new(WaveFormat.CreateIeeeFloatWaveFormat(48000, 1));
    private readonly byte[] _demoBytes = new byte[2048 * 4];
    private int _demoSample;
    private bool _loading, _closed;
    public SpectrumSettings Result { get; private set; }
    public event Action<SpectrumSettings>? PreviewChanged;

    public SpectrumSettingsWindow(SpectrumSettings initial)
    {
        _editable = initial.Clone(); _editable.Normalize(); Result = _editable.Clone();
        InitializeComponent();
        BuildFields();
        _previewTimer.Tick += (_, _) => FeedPreview();
        SourceInitialized += (_, _) => ApplyNativeTitlebar();
        RefreshPreview(false);
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
        Number(GeneralFields, "平滑程度", 0, .95, _editable.Smoothing, value => _editable.Smoothing = value, .01, "SpectrumSmoothing");
        Number(GeneralFields, "最低频率", 10, 4000, _editable.MinFrequency, value => _editable.MinFrequency = value, 10, "SpectrumMinFrequency", " Hz");
        Number(GeneralFields, "最高频率", 100, 24000, _editable.MaxFrequency, value => _editable.MaxFrequency = value, 100, "SpectrumMaxFrequency", " Hz");
        Number(GeneralFields, "绘制帧率", 15, 60, _editable.FrameRate, value => _editable.FrameRate = (int)value, 1, "SpectrumFrameRate", " fps");
        Hint(GeneralFields, "低平滑值反应更快；高平滑值更柔和。频段按对数频率分布。");

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

    private void Toggle(Panel parent, string label, bool initial, Action<bool> assign)
    {
        var checkbox = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(0, 8, 0, 10) };
        AutomationProperties.SetName(checkbox, label);
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
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = initial, TickFrequency = step, IsSnapToTickEnabled = true, Margin = new Thickness(0, 0, 0, 9) };
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

    private void Changed()
    {
        if (!_loading && !_closed) RefreshPreview(true);
    }

    private void RefreshPreview(bool notify)
    {
        foreach (var entry in _colors)
        {
            if (!Regex.IsMatch(entry.Value.Text.Trim(), @"^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$"))
            {
                ValidationText.Text = "请输入完整颜色，例如 #ADC9F0 或 #CCADC9F0。";
                SaveButton.IsEnabled = false; return;
            }
        }
        if (_editable.MinFrequency >= _editable.MaxFrequency)
        {
            ValidationText.Text = "最低频率需要小于最高频率。";
            SaveButton.IsEnabled = false; return;
        }
        _editable.ColorStart = _colors["ColorStart"].Text.Trim();
        _editable.ColorEnd = _colors["ColorEnd"].Text.Trim();
        Result = _editable.Clone(); Result.Normalize();
        PreviewSpectrum.ApplySettings(Result);
        PreviewSpectrum.IsPlaying = PreviewEnabledBox.IsChecked == true && Result.Enabled;
        ValidationText.Text = "频谱读取本机输出；DLNA 音箱的音频无法在电脑回环捕获。";
        SaveButton.IsEnabled = true;
        if (notify) PreviewChanged?.Invoke(Result.Clone());
    }

    private void FeedPreview()
    {
        if (_closed || !IsVisible || PreviewEnabledBox.IsChecked != true || !Result.Enabled) return;
        for (var i = 0; i < 2048; i++)
        {
            var time = (_demoSample + i) / 48000.0;
            var signal = .14 * Math.Sin(2 * Math.PI * 94 * time) * (.6 + .4 * Math.Sin(time * 2))
                + .08 * Math.Sin(2 * Math.PI * 750 * time) + .055 * Math.Sin(2 * Math.PI * 3500 * time);
            BinaryPrimitives.WriteInt32LittleEndian(_demoBytes.AsSpan(i * 4, 4), BitConverter.SingleToInt32Bits((float)signal));
        }
        _demoSample += 2048;
        _previewAnalyzer.ProcessPcm(_demoBytes, bins => PreviewSpectrum.SetSpectrum(bins, 48000));
    }

    private void PreviewEnabled_Click(object sender, RoutedEventArgs e)
    {
        PreviewSpectrum.IsPlaying = PreviewEnabledBox.IsChecked == true && Result.Enabled;
        if (PreviewSpectrum.IsPlaying) _previewTimer.Start(); else _previewTimer.Stop();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 40));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 40));
        _previewTimer.Start(); FeedPreview();
    }

    private void Window_Closed(object? sender, EventArgs e) { _closed = true; _previewTimer.Stop(); PreviewSpectrum.IsPlaying = false; }
    private void Reset_Click(object sender, RoutedEventArgs e) { _editable = new SpectrumSettings(); BuildFields(); RefreshPreview(true); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Save_Click(object sender, RoutedEventArgs e) { RefreshPreview(false); if (SaveButton.IsEnabled) DialogResult = true; }

    private void ApplyNativeTitlebar()
    {
        var color = (Application.Current?.Resources["Bg"] as SolidColorBrush)?.Color ?? Color.FromRgb(12, 16, 21);
        var dark = color.R + color.G + color.B < 380 ? 1 : 0;
        var handle = new WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var caption = color.R | color.G << 8 | color.B << 16;
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    private sealed record Choice(string Value, string Label) { public override string ToString() => Label; }
}
