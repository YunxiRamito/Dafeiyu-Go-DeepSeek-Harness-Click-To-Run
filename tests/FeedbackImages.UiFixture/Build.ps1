param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\source'))
$fixture = Join-Path ([IO.Path]::GetFullPath($Output)) 'source'
if (Test-Path -LiteralPath $fixture) { throw 'Use a fresh fixture output directory.' }
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
& robocopy $source $fixture /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XD bin obj 'obj-*' 'bin-*' 'dist*' 'source-obj-*' ui-review
if ($LASTEXITCODE -gt 7) { throw 'Source snapshot copy failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FeedbackImagesUiFixtureHandler.cs') -Destination $fixture
$client = Join-Path $fixture 'FeedbackClient.cs'
$text = [IO.File]::ReadAllText($client)
$original = 'http ?? NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(20))'
if (!$text.Contains($original)) { throw 'Feedback client fixture injection point changed.' }
[IO.File]::WriteAllText($client, $text.Replace($original, 'http ?? new HttpClient(new FeedbackImagesUiFixtureHandler())'))
$temp = Join-Path $Output 'build-temp'
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$env:TEMP = [IO.Path]::GetFullPath($temp)
$env:TMP = $env:TEMP
& (Join-Path $fixture 'build-winui.ps1') -OutputDirectory (Join-Path $Output 'dist') *> (Join-Path $Output 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Image QA fixture build failed; inspect build.log.' }
Write-Output ('PASS image QA fixture build: ' + (Join-Path $Output 'dist'))
