using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Embistic.Views;

public sealed record TextPromptResult(string First, string Second);

/// <summary>A small, themed prompt for names and user-supplied URLs.</summary>
public sealed class TextPromptWindow : Window
{
    private readonly TextBox _first = new(), _second = new();
    private readonly TextBlock _validation = new();
    private readonly bool _hasSecond;
    public TextPromptResult? Result { get; private set; }

    private TextPromptWindow(Window owner, string title, string firstLabel, string? secondLabel, string initialFirst, string initialSecond)
    {
        Owner = owner;
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        MinWidth = 420;
        MaxWidth = 700;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 14;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        _hasSecond = !string.IsNullOrEmpty(secondLabel);
        var content = new StackPanel { Margin = new Thickness(26) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 22) });
        content.Children.Add(new TextBlock { Text = firstLabel, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        _first.Text = initialFirst;
        AutomationProperties.SetAutomationId(_first, "PromptFirst");
        AutomationProperties.SetName(_first, firstLabel);
        content.Children.Add(_first);
        if (_hasSecond)
        {
            content.Children.Add(new TextBlock { Text = secondLabel, Margin = new Thickness(0, 18, 0, 8) });
            _second.Text = initialSecond;
            AutomationProperties.SetAutomationId(_second, "PromptSecond");
            AutomationProperties.SetName(_second, secondLabel);
            content.Children.Add(_second);
        }
        _validation.TextWrapping = TextWrapping.Wrap;
        _validation.Margin = new Thickness(0, 10, 0, 0);
        _validation.SetResourceReference(TextBlock.ForegroundProperty, "AccentInk");
        content.Children.Add(_validation);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetAutomationId(cancel, "PromptCancel");
        cancel.Click += (_, _) => DialogResult = false;
        var accept = new Button { Content = "确定", IsDefault = true };
        accept.SetResourceReference(StyleProperty, "Primary");
        AutomationProperties.SetAutomationId(accept, "PromptAccept");
        accept.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_first.Text) || (_hasSecond && string.IsNullOrWhiteSpace(_second.Text)))
            {
                _validation.Text = "请填写完整后再确定。";
                return;
            }
            Result = new(_first.Text.Trim(), _hasSecond ? _second.Text.Trim() : "");
            DialogResult = true;
        };
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        content.Children.Add(actions);
        Content = content;
        Loaded += (_, _) => { _first.Focus(); _first.SelectAll(); };
        SourceInitialized += (_, _) => ApplyChrome();
    }

    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        var window = new TextPromptWindow(owner, title, label, null, initial, "");
        return window.ShowDialog() == true ? window.Result?.First : null;
    }

    public static TextPromptResult? AskTwo(Window owner, string title, string firstLabel, string secondLabel, string initialFirst = "", string initialSecond = "")
    {
        var window = new TextPromptWindow(owner, title, firstLabel, secondLabel, initialFirst, initialSecond);
        return window.ShowDialog() == true ? window.Result : null;
    }

    private void ApplyChrome()
    {
        if (Application.Current.TryFindResource("Bg") is not SolidColorBrush brush) return;
        var color = brush.Color;
        var dark = color.R * .2126 + color.G * .7152 + color.B * .0722 < 128 ? 1 : 0;
        var handle = new WindowInteropHelper(this).Handle;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var caption = color.R | color.G << 8 | color.B << 16;
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}
