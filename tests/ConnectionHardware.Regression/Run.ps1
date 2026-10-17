param([string]$Output, [string]$Dotnet = 'G:/DeepSeek DSH/.tools/dotnet/dotnet.exe')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$Output) { $Output = Join-Path $repo '../Temp_SetupandLauncher/PlanHardware-20261017/run' }
$Output = [IO.Path]::GetFullPath($Output)
if (!$Output.StartsWith('G:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an isolated G: output directory.' }
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
$env:HARDWARE_QA = Join-Path $Output ('state-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $Output,$env:TEMP,$env:DOTNET_CLI_HOME,$env:NUGET_SCRATCH | Out-Null
$artifacts = Join-Path $Output 'artifacts'
& $Dotnet build (Join-Path $PSScriptRoot 'ConnectionHardware.Regression.csproj') -c Release --artifacts-path $artifacts *> (Join-Path $Output 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $Output 'build.log') -Tail 45; exit $LASTEXITCODE }
& $Dotnet (Join-Path $artifacts 'bin/ConnectionHardware.Regression/release/ConnectionHardware.Regression.dll') *> (Join-Path $Output 'checks.log')
$result = $LASTEXITCODE
Get-Content (Join-Path $Output 'checks.log')
exit $result
