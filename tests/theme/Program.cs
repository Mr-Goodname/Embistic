using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Embistic.Models;
using Embistic.Services;
using Embistic.Views;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main()
    {
        var app = new Application();
        var projectRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(projectRoot.FullName, "src", "Embistic", "App.xaml")))
            projectRoot = projectRoot.Parent ?? throw new DirectoryNotFoundException("Embistic source root was not found.");
        var appXaml = File.ReadAllText(Path.Combine(projectRoot.FullName, "src", "Embistic", "App.xaml"));
        var start = appXaml.IndexOf("<Application.Resources>", StringComparison.Ordinal) + "<Application.Resources>".Length;
        var end = appXaml.IndexOf("</Application.Resources>", StringComparison.Ordinal);
        var resources = appXaml[start..end];
        var dictionary = (ResourceDictionary)XamlReader.Parse(
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:local=\"clr-namespace:Embistic;assembly=ThemeChecks\" xmlns:controls=\"clr-namespace:Embistic.Controls;assembly=ThemeChecks\">" + resources + "</ResourceDictionary>");
        app.Resources = dictionary;
        var bad = new ThemeSettings { Mode = "something", AccentColor = "https://bad" }; bad.Normalize();
        Check(bad.Mode == "Dark" && bad.AccentColor == "#ADC9F0", "invalid settings normalized");
        var original = new ThemeSettings { Mode = " light ", AccentColor = " #73bcfc " }; original.Normalize();
        Check(original.Mode == "Light" && original.AccentColor == "#73BCFC", "valid settings normalized");
        var clone = original.Clone(); clone.Mode = "Dark";
        Check(original.Mode == "Light", "preview settings are independent");
        var changes = 0;
        using var service = new ThemeService();
        service.ThemeChanged += () => changes++;
        foreach (var dark in new[] { true, false })
        {
            foreach (var accent in new[] { Colors.White, Colors.Black, Colors.Blue, Colors.Yellow, Color.FromRgb(173,201,240) })
            {
                var palette = ThemeService.CreatePalette(dark, accent);
                Check(ThemeService.Contrast(palette["Text"], palette["Bg"]) > 10, "body text contrast");
                Check(ThemeService.Contrast(palette["AccentText"], palette["Accent"]) > 4.5, "primary button contrast");
                Check(ThemeService.Contrast(palette["AccentInk"], palette["Bg"]) >= 3, "accent label contrast");
            }
        }
        var window = new ThemeSettingsWindow(new ThemeSettings());
        window.Measure(new Size(800,710)); window.Arrange(new Rect(0,0,800,710)); window.UpdateLayout();
        service.Apply(new ThemeSettings { Mode = "Light", AccentColor = "#73BCFC" });
        Pump();
        Check(!service.IsDark && ((SolidColorBrush)app.Resources["Bg"]).Color == (Color)ColorConverter.ConvertFromString("#F4F7FC"), "light palette applied");
        Check((SolidColorBrush)window.Background == app.Resources["Bg"], "window dynamic background updates");
        service.Apply(new ThemeSettings { Mode = "Dark", AccentColor = "#E3AE6B" });
        Pump();
        Check(service.IsDark && service.Accent == (Color)ColorConverter.ConvertFromString("#E3AE6B"), "custom accent applied");
        Check(changes == 2, "theme change notifications");
        var mode = (ComboBox)window.FindName("ModeCombo");
        mode.SelectedValue = "Light"; Pump();
        Check(window.Result.Mode == "Light", "editor mode preview");
        var color = (TextBox)window.FindName("AccentBox");
        color.Text = "#7"; Pump();
        Check(!((Button)window.FindName("SaveButton")).IsEnabled, "incomplete color disables save");
        color.Text = "#73BCFC"; Pump();
        Check(window.Result.AccentColor == "#73BCFC", "editor custom color preview");
        var follow = (CheckBox)window.FindName("SystemAccentBox");
        follow.IsChecked = true; Pump();
        Check(window.Result.FollowSystemAccent && !color.IsEnabled, "system accent editor option");
        service.Apply(new ThemeSettings { Mode = "System", FollowSystemAccent = true });
        Check(service.IsDark == ThemeService.ReadSystemDarkMode() && service.Accent.A == 255, "system preferences read without mutation");
        Console.WriteLine($"PASS: {_checks} theme and native editor checks.");
        window.Close(); app.Shutdown();
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void Check(bool pass, string label) { if (!pass) throw new Exception("FAIL: " + label); _checks++; }
}
