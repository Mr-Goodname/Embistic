param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [string]$AssetsPath
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$AssetsPath) { $AssetsPath = Join-Path $projectRoot 'src\Embistic\obj\project.assets.json' }
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$dependencies = Get-Content -LiteralPath (Join-Path $publishRoot 'Embistic.deps.json') -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
if (!$packageFolders.Count) { throw 'No resolved NuGet package folders were found.' }

# Read the exact runtime pack versions selected by publish, including servicing
# updates, and find their original notice files in the restore's package folders.
$runtimePacks = @(
    @{ Prefix = 'runtimepack.Microsoft.NETCore.App.Runtime.'; Files = @{
        'LICENSE.TXT' = 'DotNet-Runtime-LICENSE.txt'
        'THIRD-PARTY-NOTICES.TXT' = 'DotNet-Runtime-THIRD-PARTY-NOTICES.txt'
    } },
    @{ Prefix = 'runtimepack.Microsoft.WindowsDesktop.App.Runtime.'; Files = @{
        'LICENSE' = 'WindowsDesktop-Runtime-LICENSE.txt'
    } }
)
$copies = @()
foreach ($pack in $runtimePacks) {
    $matchedPacks = @($dependencies.libraries.PSObject.Properties | Where-Object { $_.Name.StartsWith($pack.Prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($matchedPacks.Count -ne 1) { throw ('Expected one published runtime pack matching ' + $pack.Prefix) }
    $identity = $matchedPacks[0].Name.Substring('runtimepack.'.Length).Split('/')
    if ($identity.Count -ne 2 -or $identity[0] -notmatch '^[A-Za-z0-9.-]+$' -or $identity[1] -notmatch '^\d[0-9A-Za-z.+-]*$') {
        throw 'Invalid published runtime pack identity.'
    }
    $packagePath = Join-Path $identity[0].ToLowerInvariant() $identity[1].ToLowerInvariant()
    foreach ($notice in $pack.Files.GetEnumerator()) {
        $source = $null
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path (Join-Path $folder $packagePath) $notice.Key
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $source = $candidate; break }
        }
        if (!$source) { throw ('Missing ' + $notice.Key + ' for published runtime pack ' + $matchedPacks[0].Name) }
        $copies += @{ Source = $source; Name = $notice.Value }
    }
}
$licenseDirectory = Join-Path $publishRoot 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
foreach ($copy in $copies) {
    Copy-Item -LiteralPath $copy.Source -Destination (Join-Path $licenseDirectory $copy.Name) -Force
}
Write-Output ('Included ' + $copies.Count + ' original .NET and Windows Desktop runtime notice files.')
