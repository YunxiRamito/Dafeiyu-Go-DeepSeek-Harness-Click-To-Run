param(
 [Parameter(Mandatory=$true)][string]$Dist,
 [Parameter(Mandatory=$true)][string]$Output,
 [string[]]$Scenarios=@('DymExport','DymImport','DshExport','DshImport','CancelDymExport','CancelDshExport')
)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BackupDataCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
[BackupDataCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))|Out-Null
function Find-Name($Window,[string]$Name) {
 $Window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new(
  [Windows.Automation.AutomationElement]::NameProperty,$Name))
}
function Find-Id($Window,[string]$Id) {
 $Window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new(
  [Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
}
function Screenshot($Window,[string]$Path) {
 $handle=[IntPtr]::new($Window.Current.NativeWindowHandle)
 $rect=[BackupDataCapture+RECT]::new()
 [BackupDataCapture]::GetWindowRect($handle,[ref]$rect)|Out-Null
 $bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 $dc=$graphics.GetHdc()
 try{[BackupDataCapture]::PrintWindow($handle,$dc,2)|Out-Null}finally{$graphics.ReleaseHdc($dc)}
 $bitmap.Save($Path,[Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose();$bitmap.Dispose()
}
function Invoke($Element) {
 if(!$Element -or !$Element.Current.IsEnabled){throw 'Required operation button is unavailable'}
 $Element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
$artifactRoot=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $artifactRoot|Out-Null
$reports=@()
foreach($scenario in $Scenarios) {
 $fixtureRoot=Join-Path $artifactRoot $scenario
 if(Test-Path -LiteralPath $fixtureRoot){throw ('Use a fresh smoke output directory: '+$fixtureRoot)}
 New-Item -ItemType Directory -Path $fixtureRoot|Out-Null
 $env:DAFEIYU_BACKUP_UI_FIXTURE_ROOT=$fixtureRoot
 $env:DAFEIYU_BACKUP_DATA_SCENARIO=$scenario
 $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY=Join-Path $fixtureRoot 'settings'
 $env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY=Join-Path $fixtureRoot 'downloads'
 $env:DAFEIYU_INFO_CACHE_DIR=Join-Path $fixtureRoot 'cache'
 $env:TEMP=Join-Path $fixtureRoot 'temp'
 $env:TMP=$env:TEMP
 New-Item -ItemType Directory -Force -Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY,$env:TEMP|Out-Null
 @{legacyApiKeyMigrationCompleted=$true;dshRoot=(Join-Path $fixtureRoot 'fixture-dsh');notificationMuted=$true}|
  ConvertTo-Json|Set-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Encoding utf8
 $env:__COMPAT_LAYER='RunAsInvoker'
 $process=Start-Process -FilePath (Join-Path $Dist 'DeepSeek Harness.exe') -ArgumentList '--settings-preview=Developer' -PassThru -WindowStyle Hidden
 Remove-Item Env:__COMPAT_LAYER
 $report=[ordered]@{scenario=$scenario;processId=$process.Id;passed=$false;progressSamples=@();archiveEntries=@()}
 try {
  $deadline=[DateTime]::UtcNow.AddSeconds(150)
  $window=$null;$run=$null
  $command=if($scenario.EndsWith('Import')){'导入'}else{'导出'}
  $dialogTitle=if($scenario.Contains('Dym')){$command+' DYM'}elseif($command -eq '导出'){'导出 DSH ZIP'}else{'导入 DSH 数据'}
  do {
   $roots=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)
   $window=$roots|Where-Object{$_.Current.ProcessId -eq $process.Id -and $_.Current.Name -like '*设置*'}|Select-Object -First 1
   if($window){$run=Find-Name $window $command}
   if($run -and $run.Current.IsEnabled){break}
   Start-Sleep -Milliseconds 150
  }while([DateTime]::UtcNow -lt $deadline)
  if(!$run){throw ('Backup confirmation did not appear: '+$scenario)}
  Screenshot $window (Join-Path $fixtureRoot 'confirmation.png')
  Invoke $run
  $cancel=$scenario.StartsWith('Cancel')
  $canceled=$false;$progressCaptured=$false
  $popup=$null;$finished=$null
  do {
   $popup=Find-Name $window $dialogTitle
   $bar=Find-Id $popup 'BackupOperationProgress'
   $status=Find-Id $popup 'BackupOperationStatus'
   if($bar -and !$bar.Current.IsOffscreen) {
    $value=$bar.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).Current.Value
    $text=if($status){$status.Current.Name}else{''}
    if(!$report.progressSamples.Count -or $report.progressSamples[-1].text -ne $text){
     $report.progressSamples+=@{value=$value;text=$text}
    }
    if(!$progressCaptured){Screenshot $window (Join-Path $fixtureRoot 'progress.png');$progressCaptured=$true}
   }
   if($cancel -and !$canceled){
    $cancelButton=Find-Name $popup '取消操作'
    if($cancelButton -and $cancelButton.Current.IsEnabled){Invoke $cancelButton;$canceled=$true;$report.cancelClicked=$true}
   }
   $finished=Find-Name $popup '关闭'
   if($finished -and $finished.Current.IsEnabled){break}
   Start-Sleep -Milliseconds 30
  }while([DateTime]::UtcNow -lt $deadline)
  if(!$finished){throw ('Backup operation did not finish: '+$scenario)}
  if(!$progressCaptured){throw ('Visible progress missing: '+$scenario)}
  $status=Find-Id $popup 'BackupOperationStatus'
  $report.finalStatus=$status.Current.Name
  $names=@($popup.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|ForEach-Object{$_.Current.Name})
  $names|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $fixtureRoot 'ui-text.json') -Encoding utf8
  $text=$names -join "`n"
  Screenshot $window (Join-Path $fixtureRoot 'result.png')
  if($cancel){
   if(!$canceled -or $text -notmatch '已取消'){throw ('Cancellation was not reported: '+$scenario)}
   $cancelLine=@(Get-Content -LiteralPath (Join-Path $fixtureRoot 'backup-data-trace.log')|Where-Object{$_ -like 'Cancellation CTS:*'})|Select-Object -Last 1
   $requiredCts=if($scenario.Contains('Dym')){'Backup=True'}else{'DshData=True'}
   if(!$cancelLine -or !$cancelLine.ToString().Contains($requiredCts)){throw ('Cancellation token was not requested for '+$scenario+': '+$cancelLine)}
   $report.cancellationToken=$cancelLine.Trim()
   $packageDirectory=Join-Path $fixtureRoot 'packages'
   if($scenario.EndsWith('Export') -and (Test-Path -LiteralPath $packageDirectory) -and @(Get-ChildItem -LiteralPath $packageDirectory -File).Count){throw 'Canceled export left an archive or partial package'}
  }else{
   if($report.finalStatus -ne ($command+'完成')){throw ('Unexpected final status: '+$report.finalStatus)}
   if($bar.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 100){throw 'Success progress did not reach 100%'}
   if($scenario.EndsWith('Export')){
    $extension=if($scenario.StartsWith('Dym')){'*.dym'}else{'*.zip'}
    $archive=@(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'packages') -Filter $extension -File)
    if($archive.Count -ne 1 -or !$text.Contains($archive[0].FullName)){throw 'Export completion did not show the created archive save path'}
    $pathMentions=([regex]::Matches($text,[regex]::Escape($archive[0].FullName))).Count
    if($pathMentions -ne 1){throw ('Export save path must appear once in the completion dialog; found '+$pathMentions)}
    $report.archivePath=$archive[0].FullName
    $report.archiveBytes=$archive[0].Length
    if($scenario -eq 'DshExport'){
     Add-Type -AssemblyName System.IO.Compression.FileSystem
     $zip=[IO.Compression.ZipFile]::OpenRead($archive[0].FullName)
     try{$report.archiveEntries=@($zip.Entries|ForEach-Object{$_.FullName})}finally{$zip.Dispose()}
     foreach($required in @('sessions/fixture-conversation/log.jsonl','skills/fixture-skill/SKILL.md','profiles/web/node_modules/fixture-plugin/index.js')){
      if($report.archiveEntries -notcontains $required){throw ('ZIP missing '+$required)}
     }
    }
   }else{
    foreach($required in @('.dsh/sessions/fixture-conversation/log.jsonl','.dsh/skills/fixture-skill/SKILL.md','.dsh/profiles/web/node_modules/fixture-plugin/index.js')){
     if(!(Test-Path -LiteralPath (Join-Path (Join-Path $fixtureRoot 'fixture-dsh') $required))){throw ('Import missing '+$required)}
    }
   }
  }
  Invoke $finished
  $report.passed=$true
  Write-Output ('PASS '+$scenario+': visible progress, final feedback'+$(if($cancel){', cancellation cleanup'}elseif($scenario.EndsWith('Export')){', save path and archive'}else{', restored conversation/skill/plugin'}))
 }finally{
  $process.Refresh()
  if(!$process.HasExited){$process.Kill();$process.WaitForExit(10000)|Out-Null}
  $report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $fixtureRoot 'report.json') -Encoding utf8
  $reports+=$report
  foreach($name in @('fixture-dsh','source-client','packages','extracted-official-zip','settings','downloads','cache','temp')){
   $target=[IO.Path]::GetFullPath((Join-Path $fixtureRoot $name))
   if(!$target.StartsWith($fixtureRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Cleanup escaped fixture root'}
   if(Test-Path -LiteralPath $target){Remove-Item -LiteralPath $target -Recurse -Force}
  }
 }
}
$reports|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $artifactRoot 'summary.json') -Encoding utf8
Write-Output ('PASS '+$reports.Count+' native backup data smoke scenarios; generated data and packages cleaned')
