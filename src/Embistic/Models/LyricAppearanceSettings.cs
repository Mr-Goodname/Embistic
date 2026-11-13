using System.Text.RegularExpressions;

namespace Embistic.Models;

/// <summary>Serializable appearance preferences shared by the lyric page and desktop overlay.</summary>
public sealed class LyricAppearanceSettings
{
    public string FontFamily { get; set; } = "Microsoft YaHei UI, Segoe UI";
    public double MainFontSize { get; set; } = 26;
    public int MainFontWeight { get; set; } = 600;
    public double DesktopFontSize { get; set; } = 32;
    public int DesktopFontWeight { get; set; } = 600;
    public double SecondaryScale { get; set; } = .72;
    public string CurrentColor { get; set; } = "#BFD7FF";
    public string InactiveColor { get; set; } = "#8296B2";
    public string TranslationColor { get; set; } = "#C2CDDF";
    public string BackgroundColor { get; set; } = "#141B25";
    public double BackgroundOpacity { get; set; } = .25;
    public bool ShadowEnabled { get; set; } = true;
    public string ShadowColor { get; set; } = "#000000";
    public double ShadowOpacity { get; set; } = .9;
    public double ShadowBlur { get; set; } = 8;
    public double ShadowDepth { get; set; } = 1;
    public string DesktopLayout { get; set; } = "Translation";
    public string Alignment { get; set; } = "Center";
    public bool ShowTrackInfo { get; set; } = true;
    public double DesktopWidth { get; set; } = 860;
    public bool DesktopTopmost { get; set; } = true;
    public bool AnimationsEnabled { get; set; } = true;
    public bool PageTransitionsEnabled { get; set; } = true;
    public string LyricTransition { get; set; } = "Slide";
    public double TransitionDurationMs { get; set; } = 320;
    public double ScrollDurationMs { get; set; } = 520;
    public double MainFocusScale { get; set; } = 1.04;

    public LyricAppearanceSettings Clone() => (LyricAppearanceSettings)MemberwiseClone();

    public void Normalize()
    {
        var family = FontFamily?.Trim();
        FontFamily = string.IsNullOrWhiteSpace(family) || family.Length > 256
            || family.Any(char.IsControl) || family.IndexOfAny([':', '/', '\\', '#', '{', '}', ';']) >= 0
            ? "Microsoft YaHei UI, Segoe UI" : family;
        MainFontSize = Clamp(MainFontSize, 14, 64, 26);
        MainFontWeight = Math.Clamp(MainFontWeight, 100, 900);
        DesktopFontSize = Clamp(DesktopFontSize, 16, 96, 32);
        DesktopFontWeight = Math.Clamp(DesktopFontWeight, 100, 900);
        SecondaryScale = Clamp(SecondaryScale, .4, 1, .72);
        CurrentColor = Color(CurrentColor, "#BFD7FF");
        InactiveColor = Color(InactiveColor, "#8296B2");
        TranslationColor = Color(TranslationColor, "#C2CDDF");
        BackgroundColor = Color(BackgroundColor, "#141B25");
        BackgroundOpacity = Clamp(BackgroundOpacity, 0, 1, .25);
        ShadowColor = Color(ShadowColor, "#000000");
        ShadowOpacity = Clamp(ShadowOpacity, 0, 1, .9);
        ShadowBlur = Clamp(ShadowBlur, 0, 40, 8);
        ShadowDepth = Clamp(ShadowDepth, 0, 20, 1);
        DesktopLayout = Choice(DesktopLayout, "Translation", "NextLine", "Single");
        Alignment = Choice(Alignment, "Center", "Left", "Right");
        DesktopWidth = Clamp(DesktopWidth, 400, 2200, 860);
        LyricTransition = Choice(LyricTransition, "Slide", "Fade", "None");
        TransitionDurationMs = Clamp(TransitionDurationMs, 100, 1500, 320);
        ScrollDurationMs = Clamp(ScrollDurationMs, 100, 2000, 520);
        MainFocusScale = Clamp(MainFocusScale, 1, 1.2, 1.04);
    }

    private static double Clamp(double value, double min, double max, double fallback) => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
    private static string Choice(string? value, params string[] choices) => choices.FirstOrDefault(choice => string.Equals(choice, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? choices[0];
    private static string Color(string? value, string fallback)
    {
        value = value?.Trim();
        if (value is null || !Regex.IsMatch(value, @"^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant)) return fallback;
        if (value.Length == 4) value = $"#{value[1]}{value[1]}{value[2]}{value[2]}{value[3]}{value[3]}";
        return value.ToUpperInvariant();
    }
}
