param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$outputRoot = [IO.Path]::GetFullPath($Output)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../Temp_SetupandLauncher')) + [IO.Path]::DirectorySeparatorChar
if (!$outputRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be under Temp_SetupandLauncher.' }
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a fresh output directory.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$fixture = Join-Path $outputRoot 'fixture'
New-Item -ItemType Directory -Path $fixture | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FeedbackReply.WinUI.csproj'),(Join-Path $PSScriptRoot 'app.manifest'),(Join-Path $PSScriptRoot 'Fixture.cs'),(Join-Path $PSScriptRoot 'App.xaml') -Destination $fixture
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../source'))
Copy-Item -LiteralPath (Join-Path $source 'FeedbackVisualTree.cs'),(Join-Path $source 'FeedbackModels.cs'),(Join-Path $source 'FeedbackPresentation.cs') -Destination $fixture
$text = [IO.File]::ReadAllText((Join-Path $source 'SettingsWindow.Feedback.cs'))
function Extract([string]$Start,[string]$End) {
 $begin=$text.IndexOf($Start,[StringComparison]::Ordinal)
 $finish=$text.IndexOf($End,$begin+$Start.Length,[StringComparison]::Ordinal)
 if($begin -lt 0 -or $finish -lt 0){throw "Production method marker missing: $Start"}
 return $text.Substring($begin,$finish-$begin)
}
$methods = (Extract '        private async Task LoadDeveloperFeedbackCoreAsync(' '        private void RenderDeveloperFeedbackRows(') +
 (Extract '        private async Task AddDeveloperFeedbackReplyAsync(' '        private static string FeedbackCategoryLabel(')
$header = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
namespace DeepSeekHarnessLauncher { internal sealed partial class SettingsWindow {
'@
[IO.File]::WriteAllText((Join-Path $fixture 'ProductionMethods.cs'), $header+$methods+'} }')
$env:TEMP = Join-Path $outputRoot 'scratch'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = Join-Path $outputRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:NUGET_PACKAGES = 'G:/DeepSeek DSH/.nuget-packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $outputRoot 'nuget-http-cache'
$env:HTTP_PROXY = $env:HTTPS_PROXY = 'http://127.0.0.1:7890'
New-Item -ItemType Directory -Path $env:TEMP,$env:DOTNET_CLI_HOME,$env:NUGET_HTTP_CACHE_PATH | Out-Null
$dotnet = 'G:/DeepSeek DSH/.tools/dotnet/dotnet.exe'
& $dotnet build (Join-Path $fixture 'FeedbackReply.WinUI.csproj') --configuration Release --configfile (Join-Path $source 'NuGet.config') -p:UseSharedCompilation=false -m:1 -nodeReuse:false *> (Join-Path $outputRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Feedback reply WinUI build failed; inspect build.log.' }
$exe = Join-Path $fixture 'bin/Release/net8.0-windows10.0.19041.0/win-x64/FeedbackReply.WinUI.exe'
$process=Start-Process -FilePath $exe -ArgumentList ('"'+(Join-Path $outputRoot 'report.json')+'"') -PassThru -RedirectStandardOutput (Join-Path $outputRoot 'stdout.log') -RedirectStandardError (Join-Path $outputRoot 'stderr.log') -WindowStyle Hidden
if(!$process.WaitForExit(45000)){Stop-Process -Id $process.Id;throw 'Feedback reply WinUI timed out.'}
if($process.ExitCode -ne 0){throw "Feedback reply WinUI failed: $($process.ExitCode)"}
Get-Content -LiteralPath (Join-Path $outputRoot 'report.json')
