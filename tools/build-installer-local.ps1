param([string]$Version = '0.4.1',[string]$IsccPath)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$'){throw 'Invalid installer version'}
# This script only uses an existing compiler. It never downloads or installs anything.
$compiler=$null
if($IsccPath){
    if(!(Test-Path -LiteralPath $IsccPath -PathType Leaf)){throw 'The specified ISCC.exe does not exist.'}
    $compiler=(Resolve-Path -LiteralPath $IsccPath).Path
}else{
    $command=Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if($command){$compiler=$command.Source}
    if(!$compiler){
        $programFilesX86=[Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        $candidates=@(
            (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'),
            (Join-Path $programFilesX86 'Inno Setup 7\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'))
        $compiler=$candidates | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | Select-Object -First 1
    }
}
if(!$compiler){throw 'Inno Setup compiler is not installed. Use the GitHub Actions release build, or install Inno Setup yourself and pass -IsccPath.'}
if(![IO.Path]::GetFileName($compiler).Equals('ISCC.exe',[StringComparison]::OrdinalIgnoreCase)){throw 'The compiler path must point to ISCC.exe.'}
$appExe=Join-Path $projectRoot 'dist\Embistic-win-x64\Embistic.exe'
if(!(Test-Path -LiteralPath $appExe)){throw 'Publish the x64 app before building the installer.'}
& $compiler ('/DAppVersion='+$Version) ('/DProjectRoot='+$projectRoot) (Join-Path $PSScriptRoot 'installer.iss')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed.'}
Write-Output (Join-Path $projectRoot ('dist\Embistic-'+$Version+'-setup.exe'))
