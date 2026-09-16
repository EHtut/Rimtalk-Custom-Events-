<#
.SYNOPSIS
    Builds RimTalk Custom Events straight into 1.6/Assemblies.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
    .\build.ps1 -RimTalkDll "C:\path\to\RimTalk.dll"
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$RimTalkDll = ''
)

$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $projectDir 'RimTalkCustomEvents.csproj'

$args = @('build', $project, '-c', $Configuration, '-v', 'minimal')
if ($RimTalkDll -ne '') {
    $args += "-p:RimTalkDll=$RimTalkDll"
}

Write-Host "Building RimTalk Custom Events ($Configuration)..." -ForegroundColor Cyan
& dotnet @args

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

$dll = Join-Path $projectDir '1.6\Assemblies\RimTalkCustomEvents.dll'
if (Test-Path $dll) {
    $size = [math]::Round((Get-Item $dll).Length / 1KB, 1)
    Write-Host "Build OK -> 1.6\Assemblies\RimTalkCustomEvents.dll ($size KB)" -ForegroundColor Green
} else {
    Write-Host "Build reported success but $dll is missing." -ForegroundColor Yellow
    exit 1
}
