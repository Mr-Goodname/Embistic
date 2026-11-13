using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Embistic.Models;

namespace Embistic.Controls;

/// <summary>A self-contained music canvas. Root owns playback, capture and navigation.</summary>
public partial class ImmersivePlayerView : UserControl
{
    private Track? _track;
    private string _coverUrl = "", _displayTitle = "", _displayArtist = "";
    private LyricAppearanceSettings _appearance = new();
    private SpectrumSettings _spectrumSettings = new();
    private bool _playing, _rotating;
    private bool _discAnimationEnabled = true;
    private double _needleTarget = -13;
    private int _coverVersion;
    private Color _coverColor = Color.FromRgb(80, 111, 152);
    private BitmapImage? _loadingArtwork;
    private EventHandler? _downloadHandler;

    public AnimatedLyricsView LyricsView => ImmersiveLyrics;
    public SpectrumView Spectrum => ImmersiveSpectrum;
    public string SpectrumStatus { get => SpectrumStatusText.Text; set => SetSpectrumStatus(value); }
    public bool DiscAnimationEnabled
    {
        get => _discAnimationEnabled;
        set { if (_discAnimationEnabled == value) return; _discAnimationEnabled = value; SyncMotion(); }
    }
    public event Action? BackRequested;
    public event Action? FullscreenRequested;
    public event Action? LyricsSettingsRequested;
    public event Action? SpectrumSettingsRequested;
    public event Action? ImportLyricsRequested;
    public event Action? DesktopLyricsRequested;

    public ImmersivePlayerView()
    {
        InitializeComponent();
        for (var diameter = 414; diameter >= 254; diameter -= 8)
        {
            var groove = new Ellipse { Width = diameter, Height = diameter, Stroke = new SolidColorBrush(Color.FromArgb(30, 129, 147, 164)), StrokeThickness = .65 };
            Canvas.SetLeft(groove, (430 - diameter) * .5); Canvas.SetTop(groove, (430 - diameter) * .5);
            Grooves.Children.Add(groove);
        }
        ImmersiveSpectrum.SettingsRequested += () => SpectrumSettingsRequested?.Invoke();
        Loaded += (_, _) => { UpdateTint(); SyncMotion(); };
        Unloaded += (_, _) => { OptionsPopup.IsOpen = false; StopRotation(); };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { OptionsPopup.IsOpen = false; SyncMotion(); }
            else ApplyAppearance(_appearance);
        };
        ApplyAppearance(_appearance);
        ApplySpectrum(_spectrumSettings);
    }

    public void SetTrack(Track? track)
    {
        if (track?.Id == _track?.Id && track?.ImageUrl == _coverUrl && track?.Title == _displayTitle && track?.Artist == _displayArtist) return;
        _track = track;
        _coverUrl = track?.ImageUrl ?? ""; _displayTitle = track?.Title ?? ""; _displayArtist = track?.Artist ?? "";
        TrackTitle.Text = track?.Title ?? "今天，想听点什么？";
        TrackTitle.ToolTip = track?.Title;
        TrackArtist.Text = track?.Artist ?? "从你的音乐库开始";
        TrackQuality.Text = track is null ? "EMBY MUSIC" : string.IsNullOrWhiteSpace(track.QualityText) ? "DIRECT PLAY" : track.QualityText;
        _coverColor = Color.FromRgb(80, 111, 152);
        DetachArtworkHandler();
        var version = ++_coverVersion;
        BitmapSource? artwork = null;
        if (!string.IsNullOrWhiteSpace(_coverUrl))
        {
            try
            {
                // Reuse the existing decoded cover and HTTP cache; the tint samples a small
                // grid of its pixels, avoiding a second image download/decoder for the backdrop.
                artwork = (TryFindResource("Artwork") as IValueConverter)?.Convert(_coverUrl, typeof(ImageSource), null!, CultureInfo.InvariantCulture) as BitmapSource;
                if (artwork is null && Uri.TryCreate(_coverUrl, UriKind.Absolute, out var uri))
                {
                    var image = new BitmapImage();
                    image.BeginInit(); image.DecodePixelWidth = 440; image.CacheOption = BitmapCacheOption.OnDemand;
                    image.UriSource = uri; image.EndInit(); artwork = image;
                }
            }
            catch { /* The native record placeholder remains available. */ }
        }
        RecordLabelImage.ImageSource = artwork;
        BackdropImage.Source = artwork;
        RecordPlaceholder.Visibility = artwork is null ? Visibility.Visible : Visibility.Collapsed;
        if (artwork is BitmapImage downloading && downloading.IsDownloading)
        {
            _loadingArtwork = downloading;
            _downloadHandler = (_, _) =>
            {
                if (version == _coverVersion) { RecordPlaceholder.Visibility = Visibility.Collapsed; ReadCoverColor(downloading); }
                DetachArtworkHandler();
            };
            downloading.DownloadCompleted += _downloadHandler;
        }
        else if (artwork is not null) ReadCoverColor(artwork);
        UpdateTint();
        SyncMotion();
    }

    public void UpdatePlayback(long positionMs, bool playing)
    {
        // Position is intentionally not used to synthesize spectrum or record motion.
        // Its only source is Root's transport; the lyric view is updated independently.
        _playing = playing;
        ImmersiveSpectrum.IsPlaying = playing && _spectrumSettings.Enabled;
        SyncMotion();
    }

    public void ApplyAppearance(LyricAppearanceSettings appearance)
    {
        _appearance = appearance.Clone(); _appearance.Normalize();
        var displayed = _appearance.Clone();
        var baseColor = (TryFindResource("Bg") as SolidColorBrush)?.Color ?? Color.FromRgb(12, 16, 21);
        if (baseColor.R + baseColor.G + baseColor.B > 500)
        {
            // Preserve explicitly customized colors; adapt the initial pale defaults
            // so the native light theme keeps sufficient text contrast.
            if (displayed.CurrentColor == "#BFD7FF") displayed.CurrentColor = ResourceColor("Text", "#263E5D");
            if (displayed.InactiveColor == "#8296B2") displayed.InactiveColor = ResourceColor("Muted", "#667990");
            if (displayed.TranslationColor == "#C2CDDF") displayed.TranslationColor = ResourceColor("SecondaryText", "#56728F");
        }
        ImmersiveLyrics.ApplyAppearance(displayed);
        TrackTitle.FontFamily = TrackArtist.FontFamily = new FontFamily(_appearance.FontFamily);
        OptionsPopup.PopupAnimation = _appearance.AnimationsEnabled ? System.Windows.Controls.Primitives.PopupAnimation.Fade : System.Windows.Controls.Primitives.PopupAnimation.None;
        if (!_appearance.AnimationsEnabled)
        {
            NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _needleTarget = _playing && _track is not null ? 0 : -13;
            NeedleRotation.Angle = _needleTarget;
        }
        UpdateTint();
        SyncMotion();
    }

    public void ApplySpectrum(SpectrumSettings settings)
    {
        _spectrumSettings = settings.Clone(); _spectrumSettings.Normalize();
        ImmersiveSpectrum.ApplySettings(_spectrumSettings);
        SpectrumSection.Visibility = _spectrumSettings.Enabled ? Visibility.Visible : Visibility.Collapsed;
        ImmersiveSpectrum.IsPlaying = _playing && _spectrumSettings.Enabled;
    }

    public void SetSpectrumStatus(string? text)
    {
        SpectrumStatusText.Text = text ?? "";
        SpectrumStatusSurface.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ReadCoverColor(BitmapSource artwork)
    {
        try
        {
            if (artwork.PixelWidth < 1 || artwork.PixelHeight < 1) return;
            var converted = new FormatConvertedBitmap(artwork, PixelFormats.Bgra32, null, 0);
            var stride = converted.PixelWidth * 4;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            double red = 0, green = 0, blue = 0, total = 0;
            for (var y = 0; y < converted.PixelHeight; y += Math.Max(1, converted.PixelHeight / 24))
                for (var x = 0; x < converted.PixelWidth; x += Math.Max(1, converted.PixelWidth / 24))
                {
                    var offset = y * stride + x * 4;
                    var b = pixels[offset]; var g = pixels[offset + 1]; var r = pixels[offset + 2];
                    var maximum = Math.Max(r, Math.Max(g, b)); var minimum = Math.Min(r, Math.Min(g, b));
                    if (pixels[offset + 3] < 100 || maximum < 18 || minimum > 238) continue;
                    var weight = .3 + (maximum - minimum) / 255.0;
                    red += r * weight; green += g * weight; blue += b * weight; total += weight;
                }
            if (total > 0) _coverColor = Color.FromRgb((byte)(red / total), (byte)(green / total), (byte)(blue / total));
            UpdateTint();
        }
        catch { /* A failed or unsupported image cannot affect playback. */ }
    }

    private void UpdateTint()
    {
        var baseColor = (TryFindResource("Bg") as SolidColorBrush)?.Color ?? Color.FromRgb(12, 16, 21);
        AlbumTint.Color = Color.FromArgb(135,
            (byte)(_coverColor.R * .7 + baseColor.R * .3),
            (byte)(_coverColor.G * .7 + baseColor.G * .3),
            (byte)(_coverColor.B * .7 + baseColor.B * .3));
    }

    private string ResourceColor(string key, string fallback) =>
        (TryFindResource(key) as SolidColorBrush)?.Color.ToString() ?? fallback;

    private void SyncMotion()
    {
        if (!IsLoaded) return;
        if (!IsVisible)
        {
            NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            NeedleRotation.Angle = _needleTarget;
        }
        var enabled = IsVisible && _track is not null && _playing && _discAnimationEnabled && SystemParameters.ClientAreaAnimation;
        if (enabled != _rotating)
        {
            if (enabled)
            {
                var angle = DiscRotation.Angle;
                DiscRotation.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(angle, angle + 360, TimeSpan.FromSeconds(20)) { RepeatBehavior = RepeatBehavior.Forever },
                    HandoffBehavior.SnapshotAndReplace);
                _rotating = true;
            }
            else StopRotation();
        }
        var target = _playing && _track is not null ? 0 : -13;
        if (target == _needleTarget) return;
        _needleTarget = target;
        var current = NeedleRotation.Angle;
        if (Math.Abs(current - target) < .05) return;
        NeedleRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        NeedleRotation.Angle = target;
        if (IsVisible && _appearance.AnimationsEnabled && SystemParameters.ClientAreaAnimation)
            NeedleRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(_appearance.TransitionDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
                }, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopRotation()
    {
        if (!_rotating) return;
        var angle = DiscRotation.Angle % 360;
        DiscRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        DiscRotation.Angle = angle;
        _rotating = false;
    }

    private void DetachArtworkHandler()
    {
        if (_loadingArtwork is not null && _downloadHandler is not null) _loadingArtwork.DownloadCompleted -= _downloadHandler;
        _loadingArtwork = null; _downloadHandler = null;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private void Fullscreen_Click(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke();
    private void Options_Click(object sender, RoutedEventArgs e) => OptionsPopup.IsOpen = !OptionsPopup.IsOpen;
    private void LyricsSettings_Click(object sender, RoutedEventArgs e) { OptionsPopup.IsOpen = false; LyricsSettingsRequested?.Invoke(); }
    private void SpectrumSettings_Click(object sender, RoutedEventArgs e) => SpectrumSettingsRequested?.Invoke();
    private void ImportLyrics_Click(object sender, RoutedEventArgs e) { OptionsPopup.IsOpen = false; ImportLyricsRequested?.Invoke(); }
    private void DesktopLyrics_Click(object sender, RoutedEventArgs e) { OptionsPopup.IsOpen = false; DesktopLyricsRequested?.Invoke(); }
}
