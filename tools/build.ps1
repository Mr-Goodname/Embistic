# Keep native dotnet arguments intact, including -o (an ambiguous common
# PowerShell parameter when ValueFromRemainingArguments is used).
$DotnetArguments = @($args)

# The installed SDK on this development PC is missing an upstream targets file.
# Build using a project-local SDK copy when necessary; never modify Program Files.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previousSdkPath = $env:MSBuildSDKsPath
$previousBundledVersions = $env:NETCoreSdkBundledVersionsProps
$buildExitCode = 1
Push-Location -LiteralPath $projectRoot
try {
    $sdkVersion = (& dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve the .NET 10 SDK.' }
    $dotnetDirectory = Split-Path -Parent (Get-Command dotnet).Source
    $sdkDirectory = Join-Path $dotnetDirectory ('sdk\' + $sdkVersion)
    $sourceSdks = Join-Path $sdkDirectory 'Sdks'
    $cacheRelativePath = 'Microsoft.NET.Sdk\targets\Microsoft.NET.NuGetOfflineCache.targets'
    if (!(Test-Path -LiteralPath (Join-Path $sourceSdks $cacheRelativePath))) {
        $shimSdks = Join-Path (Join-Path $projectRoot ('artifacts\sdk-shim-' + $sdkVersion)) 'Sdks'
        $shimCacheTargets = Join-Path $shimSdks $cacheRelativePath
        if (!(Test-Path -LiteralPath $shimCacheTargets)) {
            New-Item -ItemType Directory -Force -Path $shimSdks | Out-Null
            Copy-Item -LiteralPath (Join-Path $sourceSdks 'Microsoft.NET.Sdk') -Destination $shimSdks -Recurse -Force
            foreach ($sourceSdk in Get-ChildItem -LiteralPath $sourceSdks -Directory) {
                if ($sourceSdk.Name -eq 'Microsoft.NET.Sdk') { continue }
                $shimSdk = Join-Path $shimSdks $sourceSdk.Name
                if (!(Test-Path -LiteralPath $shimSdk)) {
                    New-Item -ItemType Junction -Path $shimSdk -Target $sourceSdk.FullName | Out-Null
                }
            }
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sdk-fix\Microsoft.NET.NuGetOfflineCache.targets') -Destination $shimCacheTargets
        }
        # .NET 10 SDK targets also address Roslyn, Containers and SDK-level
        # targets through relative paths. Preserve that layout in the overlay.
        $shimRoot = Split-Path -Parent $shimSdks
        foreach ($sdkFolder in Get-ChildItem -LiteralPath $sdkDirectory -Directory) {
            if ($sdkFolder.Name -eq 'Sdks') { continue }
            $shimFolder = Join-Path $shimRoot $sdkFolder.Name
            if (!(Test-Path -LiteralPath $shimFolder)) {
                New-Item -ItemType Junction -Path $shimFolder -Target $sdkFolder.FullName | Out-Null
            }
        }
        foreach ($sdkFile in Get-ChildItem -LiteralPath $sdkDirectory -File | Where-Object { $_.Extension -in '.props', '.targets', '.json' }) {
            $shimFile = Join-Path $shimRoot $sdkFile.Name
            if (!(Test-Path -LiteralPath $shimFile)) {
                Copy-Item -LiteralPath $sdkFile.FullName -Destination $shimFile
            }
        }
        $env:MSBuildSDKsPath = $shimSdks
        $env:NETCoreSdkBundledVersionsProps = Join-Path $sdkDirectory 'Microsoft.NETCoreSdk.BundledVersions.props'
        Write-Host ('Using project-local SDK repair for ' + $sdkVersion + '.')
    }
    if (!$DotnetArguments -or $DotnetArguments.Count -eq 0) {
        $DotnetArguments = @('build', 'src/Embistic/Embistic.csproj', '-c', 'Release')
    }
    & dotnet @DotnetArguments
    $buildExitCode = $LASTEXITCODE
}
finally {
    $env:MSBuildSDKsPath = $previousSdkPath
    $env:NETCoreSdkBundledVersionsProps = $previousBundledVersions
    Pop-Location
}
exit $buildExitCode
