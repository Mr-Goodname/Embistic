using System.Text.RegularExpressions;

namespace Embistic.Models;

/// <summary>Independent, persisted preferences for the native audio spectrum.</summary>
public sealed class SpectrumSettings
{
    public bool Enabled { get; set; } = true;
    public string Mode { get; set; } = "Bars";
    public int BarCount { get; set; } = 64;
    public double Height { get; set; } = 90;
    public double Gain { get; set; } = 1.8;
    public double Smoothing { get; set; } = .65;
    public double MinFrequency { get; set; } = 30;
    public double MaxFrequency { get; set; } = 16000;
    public string ColorStart { get; set; } = "#ADC9F0";
    public string ColorEnd { get; set; } = "#DFCA92";
    public double Opacity { get; set; } = .7;
    public double BarWidthRatio { get; set; } = .65;
    public bool RoundedBars { get; set; } = true;
    public bool GlowEnabled { get; set; }
    public double GlowRadius { get; set; } = 8;
    public int FrameRate { get; set; } = 30;
    public bool PeakHold { get; set; }

    public SpectrumSettings Clone() => (SpectrumSettings)MemberwiseClone();

    public void Normalize()
    {
        Mode = new[] { "Bars", "Mirror", "Wave", "Line" }.FirstOrDefault(value =>
            string.Equals(value, Mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Bars";
        BarCount = Math.Clamp(BarCount, 16, 192);
        Height = Clamp(Height, 30, 240, 90);
        Gain = Clamp(Gain, .1, 8, 1.8);
        Smoothing = Clamp(Smoothing, 0, .95, .65);
        MinFrequency = Clamp(MinFrequency, 10, 4000, 30);
        MaxFrequency = Clamp(MaxFrequency, 100, 24000, 16000);
        if (MaxFrequency <= MinFrequency) MaxFrequency = Math.Min(24000, MinFrequency + 100);
        ColorStart = Color(ColorStart, "#ADC9F0");
        ColorEnd = Color(ColorEnd, "#DFCA92");
        Opacity = Clamp(Opacity, 0, 1, .7);
        BarWidthRatio = Clamp(BarWidthRatio, .15, 1, .65);
        GlowRadius = Clamp(GlowRadius, 0, 32, 8);
        FrameRate = Math.Clamp(FrameRate, 15, 60);
    }

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string Color(string? value, string fallback)
    {
        value = value?.Trim();
        if (value is null || !Regex.IsMatch(value, @"^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")) return fallback;
        return value.ToUpperInvariant();
    }
}
