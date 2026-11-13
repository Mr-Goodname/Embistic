using System.Text.RegularExpressions;

namespace Embistic.Models;

public sealed class ThemeSettings
{
    public string Mode { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#ADC9F0";
    public bool FollowSystemAccent { get; set; }
    public ThemeSettings Clone() => (ThemeSettings)MemberwiseClone();

    public void Normalize()
    {
        Mode = new[] { "Dark", "Light", "System" }.FirstOrDefault(x =>
            string.Equals(x, Mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Dark";
        AccentColor = NormalizeColor(AccentColor) ?? "#ADC9F0";
    }

    public static string? NormalizeColor(string? value)
    {
        value = value?.Trim();
        if (value is null || !Regex.IsMatch(value, "^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)) return null;
        return value.ToUpperInvariant();
    }
}
