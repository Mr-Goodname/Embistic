$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publish=Join-Path $projectRoot 'dist\Embistic-win-x64'
$archive=Join-Path $projectRoot 'dist\Embistic-win-x64.zip'
& powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') publish src/Embistic/Embistic.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $publish
if($LASTEXITCODE -ne 0){throw 'Publish failed'}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $publish -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $publish -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $publish -Recurse -Force
# The native NuGet package copies both architectures. This app is x64 only.
$x86=[IO.Path]::GetFullPath((Join-Path $publish 'libvlc\win-x86'))
if(!$x86.StartsWith($publish+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid native output path'}
if(Test-Path -LiteralPath $x86){Remove-Item -LiteralPath $x86 -Recurse -Force}
Compress-Archive -Path $publish -DestinationPath $archive -CompressionLevel Optimal -Force
Write-Output $publish
Write-Output $archive
