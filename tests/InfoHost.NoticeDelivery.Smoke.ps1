param([Parameter(Mandatory)][string]$Dist, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$dotnet = 'G:\DeepSeek DSH\.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$helperRoot = [IO.Path]::GetFullPath($Dist)
if (Test-Path -LiteralPath (Join-Path $helperRoot 'info-host')) { $helperRoot = Join-Path $helperRoot 'info-host' }
$project = Join-Path $PSScriptRoot 'InfoHost.NoticeDelivery.UiFixture\InfoHost.NoticeDelivery.UiFixture.csproj'
& $dotnet run --project $project -- $helperRoot ([IO.Path]::GetFullPath($Output))
if ($LASTEXITCODE -ne 0) { throw 'First-message notice rendering/receipt fixture failed.' }
