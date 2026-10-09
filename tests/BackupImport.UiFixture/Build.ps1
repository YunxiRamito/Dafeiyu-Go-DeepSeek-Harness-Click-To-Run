param([Parameter(Mandatory=$true)][string]$Output, [Parameter(Mandatory=$true)][string]$Dotnet)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\source'))
$fixture = Join-Path ([IO.Path]::GetFullPath($Output)) 'source'
if (Test-Path -LiteralPath $fixture) { throw 'Use a fresh fixture output directory.' }
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
& robocopy $source $fixture /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /XD bin obj 'obj-*' 'bin-*' 'dist*' 'source-obj-*' ui-review
if ($LASTEXITCODE -gt 7) { throw 'Source snapshot copy failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BackupImportUiFixture.cs') -Destination $fixture
$window = Join-Path $fixture 'SettingsWindow.xaml.cs'
$text = [IO.File]::ReadAllText($window)
$original = '_host = host ?? CreatePreviewHost();'
if (!$text.Contains($original)) { throw 'Backup UI fixture injection point changed.' }
[IO.File]::WriteAllText($window, $text.Replace($original, '_host = host ?? BackupImportUiFixture.CreateHost(CreatePreviewHost());'))
$windowText = [IO.File]::ReadAllText($window)
$originalPreview = '                LoadDeveloperCenter();'
if (!$windowText.Contains($originalPreview)) { throw 'Notification UI fixture injection point changed.' }
[IO.File]::WriteAllText($window, $windowText.Replace($originalPreview, $originalPreview + "`r`n                BackupImportUiFixture.SeedNotification(this);`r`n                BackupImportUiFixture.ScheduleBackupDialogSmoke(this);`r`n                BackupImportUiFixture.ScheduleBackupDataSmoke(this);"))
$backupUi = Join-Path $fixture 'SettingsWindow.BackupImport.cs'
$backupUiText = [IO.File]::ReadAllText($backupUi)
$cancelOriginal = "            _dshDataCancellation?.Cancel();"
if (!$backupUiText.Contains($cancelOriginal)) { throw 'Backup cancellation fixture injection point changed.' }
[IO.File]::WriteAllText($backupUi, $backupUiText.Replace($cancelOriginal, $cancelOriginal + "`r`n            BackupImportUiFixture.RecordCancellation(this);"))
& $Dotnet build (Join-Path $fixture 'DeepSeekHarness.csproj') -p:Platform=x64 -v:q -p:WarningLevel=0
if ($LASTEXITCODE -ne 0) { throw 'Backup UI fixture build failed.' }
Write-Output ('PASS backup UI fixture build: ' + (Join-Path $fixture 'bin\x64\Debug\net8.0-windows10.0.19041.0\DeepSeek Harness.exe'))
