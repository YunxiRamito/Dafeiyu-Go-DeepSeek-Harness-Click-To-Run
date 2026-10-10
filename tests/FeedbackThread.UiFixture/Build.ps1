param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($Output)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\Temp_SetupandLauncher')) + '\'
if (!$outputRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be under Temp_SetupandLauncher.' }
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a fresh output directory.' }
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\source'))
$fixture = Join-Path $outputRoot 'source'
New-Item -ItemType Directory -Path $fixture | Out-Null
& robocopy $source $fixture /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XD bin obj 'obj-*' 'bin-*' 'dist*' 'source-obj-*' ui-review
if ($LASTEXITCODE -gt 7) { throw 'Source snapshot copy failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FeedbackThreadUiFixtureHandler.cs') -Destination $fixture
$transport = Join-Path $fixture 'NoticeTransport.cs'
$text = [IO.File]::ReadAllText($transport)
$original = 'new HttpClient(new NoticeReadRetryHandler(CreateHandler(false), CreateHandler(true)))'
if (!$text.Contains($original)) { throw 'Fixture injection point changed.' }
[IO.File]::WriteAllText($transport, $text.Replace($original, 'new HttpClient(new FeedbackThreadUiFixtureHandler())'))
$env:TEMP = Join-Path $outputRoot 'scratch'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $outputRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $outputRoot 'nuget-http-cache'
New-Item -ItemType Directory -Path $env:TEMP,$env:DOTNET_CLI_HOME,$env:NUGET_HTTP_CACHE_PATH | Out-Null
$dotnet = 'G:\DeepSeek DSH\.tools\dotnet\dotnet.exe'
$dist = Join-Path $outputRoot 'dist'
& $dotnet publish (Join-Path $fixture 'DeepSeekHarness.csproj') --configuration Release --runtime win-x64 --self-contained false --configfile (Join-Path $fixture 'NuGet.config') --output $dist '-p:AssemblyName=DeepSeek Harness.Core' -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false -m:1 -nodeReuse:false *> (Join-Path $outputRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Feedback UI fixture build failed; inspect build.log.' }
Write-Output ('PASS isolated feedback fixture build: ' + $dist)
