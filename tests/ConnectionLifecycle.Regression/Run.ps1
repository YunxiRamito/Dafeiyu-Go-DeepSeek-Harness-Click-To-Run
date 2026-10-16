param([string]$Output, [string]$Dotnet = 'G:/DeepSeek DSH/.tools/dotnet/dotnet.exe')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $Output) { $Output = Join-Path $repo '../Temp_SetupandLauncher/Feedback-20261016/connection' }
$Output = [IO.Path]::GetFullPath($Output)
if (-not $Output.StartsWith('G:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an isolated G: output directory.' }
$env:DOTNET_ROOT = Split-Path $Dotnet
$env:DOTNET_CLI_HOME = Join-Path $Output 'dotnet-home'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = 'G:/DeepSeek DSH/.nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $Output 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $Output 'nuget-plugins'
$env:NUGET_SCRATCH = Join-Path $Output 'nuget-scratch'
$env:TEMP = Join-Path $Output 'temp'
$env:TMP = $env:TEMP
$env:TMPDIR = $env:TEMP
$env:CONNECTION_REPO = $repo
$env:CONNECTION_QA = Join-Path $Output ('lifecycle-state/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $Output,$env:TEMP,$env:DOTNET_CLI_HOME,$env:NUGET_SCRATCH | Out-Null
$artifacts = Join-Path $Output 'artifacts'
& $Dotnet build (Join-Path $PSScriptRoot 'ConnectionLifecycle.Regression.csproj') -c Release --artifacts-path $artifacts *> (Join-Path $Output 'lifecycle-build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Output 'lifecycle-build.log') -Tail 40; exit $LASTEXITCODE }
$dll = Join-Path $artifacts 'bin/ConnectionLifecycle.Regression/release/ConnectionLifecycle.Regression.dll'
& $Dotnet $dll *> (Join-Path $Output 'lifecycle-regression.log')
$result = $LASTEXITCODE
Get-Content (Join-Path $Output 'lifecycle-regression.log')
exit $result
