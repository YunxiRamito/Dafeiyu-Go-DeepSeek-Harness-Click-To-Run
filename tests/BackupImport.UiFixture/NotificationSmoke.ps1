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
public static class NoticeUiCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out RECT rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
[NoticeUiCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$fixtureRoot = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
$env:DAFEIYU_BACKUP_UI_FIXTURE_ROOT = $fixtureRoot
$env:DAFEIYU_NOTIFICATION_UI_FIXTURE = '1'
$env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY = Join-Path $fixtureRoot 'settings'
$env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY = Join-Path $fixtureRoot 'downloads'
$env:DAFEIYU_INFO_CACHE_DIR = Join-Path $fixtureRoot 'cache'
New-Item -ItemType Directory -Force -Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY | Out-Null
@{
 legacyApiKeyMigrationCompleted=$true
 dshRoot=(Join-Path $fixtureRoot 'fixture-dsh')
 notificationMuted=$true
 apiKeyProtected=''
 gitHubTokenProtected=''
 adminTokenProtected=''
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Encoding utf8

function Find-Control($Window,[string]$Name,[string]$Id='') {
 $property = if($Id) {[Windows.Automation.AutomationElement]::AutomationIdProperty} else {[Windows.Automation.AutomationElement]::NameProperty}
 $value = if($Id) {$Id} else {$Name}
 return $Window.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new($property,$value))
}
function Screenshot($Window,[string]$Name) {
 $handle=[IntPtr]::new($Window.Current.NativeWindowHandle)
 $rect=[NoticeUiCapture+RECT]::new()
 [NoticeUiCapture]::GetWindowRect($handle,[ref]$rect) | Out-Null
 $bitmap=[Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 $dc=$graphics.GetHdc()
 try {[NoticeUiCapture]::PrintWindow($handle,$dc,2) | Out-Null}
 finally {$graphics.ReleaseHdc($dc)}
 $bitmap.Save((Join-Path $fixtureRoot ($Name+'.png')),[Drawing.Imaging.ImageFormat]::Png)
 $graphics.Dispose()
 $bitmap.Dispose()
}
function Wait-Window([int]$Id,[string]$Contains) {
 $deadline=[DateTime]::UtcNow.AddSeconds(30)
 do {
  $roots=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)
  $window=$roots | Where-Object {$_.Current.ProcessId -eq $Id -and $_.Current.Name.Contains($Contains)} | Select-Object -First 1
  if($window) {return $window}
  Start-Sleep -Milliseconds 200
 } while([DateTime]::UtcNow -lt $deadline)
 throw "Window did not appear: process=$Id name=$Contains"
}
$env:__COMPAT_LAYER='RunAsInvoker'
$process=Start-Process -FilePath (Join-Path $Dist 'DeepSeek Harness.exe') -ArgumentList '--settings-preview=Developer' -PassThru
Remove-Item Env:__COMPAT_LAYER
try {
 $window=Wait-Window $process.Id '设置'
 Start-Sleep -Seconds 3
 foreach($name in @('隔离预览通知','按钮 1 · 查看反馈（打开设置 · About）')) {
  if(!(Find-Control $window $name)){throw "Current notification missing: $name"}
 }
 $preview=Find-Control $window '预览通知' 'DeveloperNotificationPreviewButton'
 if(!$preview -or !$preview.Current.IsEnabled){throw 'Current notification preview not enabled'}
 $body=@($window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
  Where-Object {$_.Current.Name -like '*固定通知正文来自隔离 GUI 测试*'})
 if(!$body.Count){throw 'Current notification rendered Markdown missing'}
 $metrics='收到人数：37 · 展示：21 · 已读：13 · 按钮点击：5 / 2'
 if(!(Find-Control $window $metrics)){throw 'Isolated notification metrics missing'}
 Screenshot $window 'current-notification'
 $preview.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 $helperDeadline=[DateTime]::UtcNow.AddSeconds(25)
 $helper=$null
 do {
  $helpers=Get-CimInstance Win32_Process -Filter "Name='DafeiyuGo.Info.exe'" |
   Where-Object {$_.ParentProcessId -eq $process.Id -and $_.ExecutablePath.StartsWith($fixtureRoot,[StringComparison]::OrdinalIgnoreCase)}
  $helper=$helpers | Select-Object -First 1
  if($helper){break}
  Start-Sleep -Milliseconds 200
 } while([DateTime]::UtcNow -lt $helperDeadline)
 if(!$helper){throw 'Isolated preview helper never appeared'}
 $notice=Wait-Window $helper.ProcessId '信息'
 Start-Sleep -Seconds 2
 if(!(Find-Control $notice '隔离预览通知')){throw 'Info preview title missing'}
 $close=Find-Control $notice '关闭'
 if(!$close){throw 'Preview is not a local notice'}
 if(Find-Control $notice '已读'){throw 'Preview has real receipt control'}
 Screenshot $notice 'notification-preview'
 $action=Find-Control $notice '查看反馈'
 if(!$action){throw 'Preview action missing'}
 $action.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Start-Sleep -Seconds 2
 if(!(Find-Control $window $metrics)){throw 'Preview altered current metrics'}
 if(!(Find-Control $window '通知管理')){throw 'Preview navigated real action'}
 Screenshot $window 'notification-after-preview'
 $rows=foreach($node in $window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)){
  if($node.Current.Name){[pscustomobject]@{Name=$node.Current.Name;Id=$node.Current.AutomationId;Offscreen=$node.Current.IsOffscreen}}
 }
 $rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'notification-uia.json') -Encoding utf8
 Write-Output 'PASS current notification title, Markdown host, full action target, local information-window preview, unchanged metrics and no action navigation'
} finally {
 $process.Refresh()
 if(!$process.HasExited){$process.Kill();$process.WaitForExit(10000)|Out-Null}
 Get-CimInstance Win32_Process -Filter "Name='DafeiyuGo.Info.exe'" |
  Where-Object {$_.ParentProcessId -eq $process.Id -and $_.ExecutablePath.StartsWith($fixtureRoot,[StringComparison]::OrdinalIgnoreCase)} |
  ForEach-Object {Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue}
}
