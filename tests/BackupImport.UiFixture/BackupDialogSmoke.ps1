param(
 [Parameter(Mandatory=$true)][string]$Dist,
 [Parameter(Mandatory=$true)][string]$Output
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BackupDialogCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
[BackupDialogCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$fixtureRoot=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
$env:DAFEIYU_BACKUP_UI_FIXTURE_ROOT=$fixtureRoot
$env:DAFEIYU_BACKUP_DIALOG_FIXTURE='1'
$env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY=Join-Path $fixtureRoot 'settings'
$env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY=Join-Path $fixtureRoot 'downloads'
$env:DAFEIYU_INFO_CACHE_DIR=Join-Path $fixtureRoot 'cache'
New-Item -ItemType Directory -Force -Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY | Out-Null
@{legacyApiKeyMigrationCompleted=$true;dshRoot=(Join-Path $fixtureRoot 'fixture-dsh');notificationMuted=$true} |
 ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Encoding utf8
function Find-Text($Window,[string]$Text) {
 return $Window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new(
  [Windows.Automation.AutomationElement]::NameProperty,$Text))
}
function Screenshot($Window,[string]$Path) {
 $handle=[IntPtr]::new($Window.Current.NativeWindowHandle)
 $rect=[BackupDialogCapture+RECT]::new()
 [BackupDialogCapture]::GetWindowRect($handle,[ref]$rect)|Out-Null
 $bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 $dc=$graphics.GetHdc()
 try{[BackupDialogCapture]::PrintWindow($handle,$dc,2)|Out-Null}finally{$graphics.ReleaseHdc($dc)}
 $bitmap.Save($Path,[Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose();$bitmap.Dispose()
}
$env:__COMPAT_LAYER='RunAsInvoker'
$process=Start-Process -FilePath (Join-Path $Dist 'DeepSeek Harness.exe') -ArgumentList '--settings-preview=Developer' -PassThru
Remove-Item Env:__COMPAT_LAYER
try {
 $deadline=[DateTime]::UtcNow.AddSeconds(30)
 $window=$null
 do {
  $roots=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)
  $window=$roots|Where-Object{$_.Current.ProcessId -eq $process.Id -and $_.Current.Name -like '*设置*'}|Select-Object -First 1
  if($window){break}
  Start-Sleep -Milliseconds 200
 } while([DateTime]::UtcNow -lt $deadline)
 if(!$window){throw 'Settings window did not appear'}
 $dialog=$null
 do {
  $dialog=Find-Text $window '导入 DSH 数据'
  if($dialog){break}
  Start-Sleep -Milliseconds 200
 } while([DateTime]::UtcNow -lt $deadline)
 if(!$dialog){throw 'Backup operation dialog did not open'}
 $screenshotPath=Join-Path $fixtureRoot 'backup-dialog-before.png'
 Screenshot $window $screenshotPath
 $run=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new(
  [Windows.Automation.AutomationElement]::NameProperty,'模拟导入'))
 if(!$run){throw 'Fixture operation button missing'}
 $run.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 $finish=[DateTime]::UtcNow.AddSeconds(10)
 do {
  $status=@($window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|
   Where-Object{$_.Current.Name -eq '模拟导入失败'})
  if($status.Count){break}
  Start-Sleep -Milliseconds 100
 } while([DateTime]::UtcNow -lt $finish)
 if(!$status.Count){throw 'Final error status was not retained in the operation dialog'}
 $bar=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new(
  [Windows.Automation.AutomationElement]::AutomationIdProperty,'BackupOperationProgress'))
 if(!$bar -or $bar.GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).Current.Value -ne 80){throw 'Final progress value was not retained'}
 $errorText=@($window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)|
  Where-Object{($_.Current.Name -replace '\s','') -eq '模拟导入失败：fixtureerrordetail'})
 if(!$errorText.Count){throw 'Detailed operation error missing from dialog'}
 if(!(Find-Text $window '关闭')){throw 'Completed operation dialog close button missing'}
 Screenshot $window (Join-Path $fixtureRoot 'backup-dialog-result.png')
 Write-Output 'PASS backup confirmation dialog shows retained progress, detailed error and close state using isolated fake operation'
} finally {
 $process.Refresh()
 if(!$process.HasExited){$process.Kill();$process.WaitForExit(10000)|Out-Null}
}
