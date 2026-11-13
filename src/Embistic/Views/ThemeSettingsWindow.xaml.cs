using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Embistic.Models;
using Embistic.Services;

namespace Embistic.Views;

public partial class ThemeSettingsWindow : Window
{
    private ThemeSettings _editable;
    private bool _initialized, _closed, _queued;
    public event Action<ThemeSettings>? PreviewChanged;
    public ThemeSettings Result { get; private set; }
    public ThemeSettingsWindow(ThemeSettings initial)
    {
        _editable = initial?.Clone() ?? new ThemeSettings();
        _editable.Normalize();
        Result = _editable.Clone();
        InitializeComponent();
        ModeCombo.ItemsSource = new[]
        {
            new ModeChoice("Dark", "暗色"), new ModeChoice("Light", "亮色"), new ModeChoice("System", "跟随系统")
        };
        DataContext = _editable;
        _initialized = true;
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dark = _editable.Mode == "Dark" || (_editable.Mode == "System" && ThemeService.ReadSystemDarkMode()) ? 1 : 0;
        var handle = new WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var area = SystemParameters.WorkArea;
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 50));
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 50));
        RefreshPreview();
    }
    private void Value_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _closed || _queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _queued = false;
            if (!_closed) RefreshPreview();
        }));
    }
    private bool RefreshPreview()
    {
        AccentBox.IsEnabled = !_editable.FollowSystemAccent;
        var custom = ThemeSettings.NormalizeColor(_editable.AccentColor);
        var valid = _editable.FollowSystemAccent || custom is not null;
        SaveButton.IsEnabled = valid;
        ValidationMessage.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        ValidationMessage.Text = valid ? "" : "请输入六位颜色，例如 #73BCFC。";
        if (!valid) return false;
        var candidate = _editable.Clone();
        candidate.Normalize();
        var accent = candidate.FollowSystemAccent ? ThemeService.ReadSystemAccent() : (Color)ColorConverter.ConvertFromString(candidate.AccentColor);
        AccentChip.Background = new SolidColorBrush(accent);
        SystemAccentLabel.Text = candidate.FollowSystemAccent ? $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}" : "";
        PreviewLabel.Text = candidate.Mode switch
        {
            "Dark" => "暗色界面 · 更适合夜间聆听",
            "Light" => "亮色界面 · 清晰、轻盈",
            _ => $"跟随 Windows · 当前{(ThemeService.ReadSystemDarkMode() ? "暗色" : "亮色")}"
        };
        Result = candidate;
        PreviewChanged?.Invoke(candidate.Clone());
        return true;
    }
    private void ColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        SystemAccentBox.IsChecked = false;
        AccentBox.Text = color;
    }
    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _editable = new ThemeSettings();
        DataContext = null; DataContext = _editable;
        RefreshPreview();
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (RefreshPreview()) DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Window_Closed(object? sender, EventArgs e) => _closed = true;
    private sealed record ModeChoice(string Value, string Label) { public override string ToString() => Label; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
