param(
    [string]$Dotnet = '',
    [string]$Output = '',
    [switch]$SkipStyle
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$workspace = Split-Path $repo -Parent
if (!$Dotnet) { $Dotnet = Join-Path $workspace 'Temp_SetupandLauncher/DotnetSdk/dotnet.exe' }
if (!$Output) { $Output = Join-Path $workspace 'Temp_SetupandLauncher/Integration-20261014/Window' }
$Output = [IO.Path]::GetFullPath($Output)
$env:TEMP = Join-Path $Output 'tmp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $Output 'dotnet-home'
$env:DOTNET_ROOT = Split-Path $Dotnet -Parent
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $Output 'nuget-http'
$env:NUGET_SCRATCH = Join-Path $Output 'nuget-scratch'
New-Item -ItemType Directory -Force -Path $env:TEMP, $env:DOTNET_CLI_HOME, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_SCRATCH | Out-Null
if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = 'G:/DeepSeek DSH/.nuget-packages' }

function Invoke-Checked([string]$Executable, [string[]]$Arguments, [string]$LogName) {
    $lines = & $Executable @Arguments 2>&1
    $code = $LASTEXITCODE
    $lines | Tee-Object -FilePath (Join-Path $Output $LogName)
    if ($code -ne 0) { throw "$Executable failed with exit code $code" }
}

foreach ($fixture in @(@{Name='DshWindow.Regression';Prefix='policy'}, @{Name='DshWindow.Compile';Prefix='compile'})) {
    $prefix = $fixture.Prefix
    $project = Join-Path $repo "tests/$($fixture.Name)/$($fixture.Name).csproj"
    Invoke-Checked $Dotnet @('build', $project, '-c', 'Release', '-p:Platform=x64',
        "-p:BaseIntermediateOutputPath=$Output/$prefix-obj/",
        "-p:MSBuildProjectExtensionsPath=$Output/$prefix-obj/",
        "-p:OutputPath=$Output/$prefix-bin/", '--nologo') "$prefix-build.log"
    Invoke-Checked $Dotnet @("$Output/$prefix-bin/$($fixture.Name).dll") "$prefix-behavior.log"
}
if (!$SkipStyle) {
    Invoke-Checked "$Output/compile-bin/DshWindow.Compile.exe" @('--style') 'style-behavior.log'
    $env:DAFEIYU_WINDOW_MENU_QA = $Output
    Invoke-Checked "$Output/compile-bin/DshWindow.Compile.exe" @('--menu') 'menu-behavior.log'
}
