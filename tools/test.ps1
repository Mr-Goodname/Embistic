param([switch]$IncludeMedia)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projects = @(
    'auth/AuthChecks.csproj',
    'updates/UpdateChecks.csproj',
    'library/LibraryChecks.csproj',
    'network-cache/NetworkCacheChecks.csproj',
    'lyrics/LyricsTests.csproj',
    'dlna/DlnaChecks.csproj',
    'appearance/AppearanceTests.csproj',
    'desktop-lyrics/DesktopLyricsChecks.csproj',
    'animated-lyrics/AnimatedLyricsChecks.csproj',
    'theme/ThemeChecks.csproj',
    'spectrum/SpectrumChecks.csproj',
    'sliders/SliderChecks.csproj',
    'background/BackgroundChecks.csproj',
    'immersive-layout/ImmersiveLayoutChecks.csproj',
    'inline-settings/InlineSettingsChecks.csproj',
    'playlists/PlaylistChecks.csproj',
    'navigation/NavigationChecks.csproj',
    'integration/IntegrationChecks.csproj'
)
if ($IncludeMedia) { $projects += 'media/MediaSmoke.csproj' }
foreach ($project in $projects) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') run --project (Join-Path $projectRoot ('tests/' + $project)) -c Release
    if ($LASTEXITCODE -ne 0) { throw ('Checks failed: ' + $project) }
}
Write-Output ('All ' + $projects.Count + ' check projects passed.')
