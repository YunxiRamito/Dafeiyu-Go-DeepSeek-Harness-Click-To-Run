param([string]$Dist, [string]$Page = 'Patches', [string]$Output, [switch]$ReadOnlyPreview, [string]$SearchQuery, [string]$StartPage, [switch]$MessageEditors, [switch]$SourceChoices, [switch]$ExtensionTabs)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SettingsCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@
[SettingsCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
New-Item -ItemType Directory -Force $Output | Out-Null
$env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY = Join-Path $Output 'settings'
$env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY = Join-Path ([IO.Path]::GetFullPath($Output)) 'downloads'
New-Item -ItemType Directory -Force $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY | Out-Null
$fixtureFile = Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json'
if (!(Test-Path -LiteralPath $fixtureFile)) {
 # Keep first-run migration and root detection away from the user's installed DSH data.
 @{legacyApiKeyMigrationCompleted=$true;dshRoot=(Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'fixture-dsh');apiKeyProtected='';gitHubTokenProtected='';adminTokenProtected=''} |
  ConvertTo-Json | Set-Content -LiteralPath $fixtureFile -Encoding utf8
}
try {
 # The read-only settings preview needs no administrative operations.
 if ($ReadOnlyPreview) { $env:__COMPAT_LAYER = 'RunAsInvoker' }
 $initialPage = if ($StartPage) { $StartPage } else { $Page }
 $launcherExe = Join-Path $Dist 'DeepSeek Harness.Core.exe'
 if (!(Test-Path -LiteralPath $launcherExe)) { $launcherExe = Join-Path $Dist 'DeepSeek Harness.exe' }
 if (!(Test-Path -LiteralPath $launcherExe)) { throw "Launcher executable missing from Dist: $Dist" }
 $process = Start-Process -FilePath $launcherExe -ArgumentList "--settings-preview=$initialPage" -PassThru
} finally { if ($ReadOnlyPreview) { Remove-Item Env:__COMPAT_LAYER -ErrorAction SilentlyContinue } }
try {
 $deadline = [datetime]::UtcNow.AddSeconds(35)
 $element = $null
 while ([datetime]::UtcNow -lt $deadline) {
  $process.Refresh()
  if ($process.HasExited) { throw "Preview exited: $($process.ExitCode)" }
  $roots = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.Condition]::TrueCondition)
  $element = $roots | Where-Object { $_.Current.ProcessId -eq $process.Id -and $_.Current.Name -eq 'Dafeiyu-Go 设置' } | Select-Object -First 1
  if ($element) { break }
  Start-Sleep -Milliseconds 250
 }
 if (!$element) { throw 'Settings preview window never appeared' }
 Start-Sleep -Seconds 5
 if ($SearchQuery) {
  $box = $element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'SettingsSearchBox'))
  if (!$box) { throw 'Search box missing' }
  $editor = $box.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))
  if (!$editor) { $editor=$box }
  $editor.SetFocus()
  $editor.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($SearchQuery)
  Start-Sleep -Milliseconds 800
  $results = $element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'SettingsSearchResults'))
  $result = $results.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
  if (!$result) { throw "No search result: $SearchQuery" }
  $invoke = $null
  if ($result.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern,[ref]$invoke)) {
   $invoke.Invoke()
  } else {
   [SettingsCapture]::SetForegroundWindow([IntPtr]::new($element.Current.NativeWindowHandle)) | Out-Null
   $point = $result.GetClickablePoint()
   [SettingsCapture]::SetCursorPos([int]$point.X,[int]$point.Y) | Out-Null
   [SettingsCapture]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
   [SettingsCapture]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
  }
  Start-Sleep -Seconds 2
  $searchPanel = $element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'SettingsSearchPanel'))
  if ($searchPanel -and !$searchPanel.Current.IsOffscreen) { throw 'Search result did not activate its destination' }
  if ($SearchQuery -in @('hdfwqjs','后端服务器加速')) {
   $backend = $element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'UpdateSourceComboBox'))
   if (!$backend -or $backend.Current.IsOffscreen) { throw 'Backend source search did not reveal the selectable source' }
  }
  if ($SearchQuery -in @('tsgg','推送公告')) {
   $push=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'DeveloperAnnouncementPushButton'))
   if (!$push -or $push.Current.IsOffscreen -or $push.Current.Name -ne '推送公告') { throw 'Announcement push search did not select and reveal its management tab' }
   Write-Host "PASS announcement push search navigation: $SearchQuery"
  }
  if ($SearchQuery -in @('反馈处理','fkcl')) {
   $refresh=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'DeveloperFeedbackRefreshButton'))
   if(!$refresh -or $refresh.Current.IsOffscreen){throw 'Developer feedback search did not reveal its management panel'}
   $module=$element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'反馈处理')) | Where-Object {$_.Current.ControlType -eq [Windows.Automation.ControlType]::ListItem -and !$_.Current.IsOffscreen} | Select-Object -First 1
   if(!$module -or !$module.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected){throw 'Developer feedback search selected the wrong module'}
   $guard=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'管理员 Token 未填写，请先在 API 与翻译中保存管理员 Token。'))
   if(!$guard -or $guard.Current.IsOffscreen){throw 'Developer feedback management did not retain its missing-token guard'}
   Write-Host "PASS developer feedback module and missing-token guard: $SearchQuery"
  }
 }
 $nodes = $element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
 $rows = foreach ($node in $nodes) { if ($node.Current.Name) { [pscustomobject]@{Name=$node.Current.Name;Type=$node.Current.ControlType.ProgrammaticName;Offscreen=$node.Current.IsOffscreen;Bounds=$node.Current.BoundingRectangle.ToString()} } }
 if ($Page -eq 'Alerts') {
  foreach ($text in @('通知静音','更新提醒','插件更新提醒','充值提醒','今日消费提醒','余额提醒')) {
   if (!$rows.Where({$_.Name -eq $text -and !$_.Offscreen}).Count) { throw "Alert control missing: $text" }
  }
  foreach ($amount in @('¥5','¥10','¥20','¥50','¥1')) {
   if (!$rows.Where({$_.Name -eq $amount -and $_.Type -eq 'ControlType.CheckBox' -and !$_.Offscreen}).Count) { throw "Alert threshold clipped: $amount" }
  }
  $customChecks=@($nodes | Where-Object {$_.Current.Name -eq '自定义金额' -and $_.Current.ControlType -eq [Windows.Automation.ControlType]::CheckBox -and !$_.Current.IsOffscreen} | Sort-Object {$_.Current.BoundingRectangle.Left})
  foreach($index in 0,1) {
   $inputName=if($index -eq 0){'自定义消费提醒金额'}else{'自定义余额提醒金额'}
   $input=$nodes | Where-Object {$_.Current.Name -eq $inputName -and !$_.Current.IsOffscreen} | Select-Object -First 1
   if($customChecks.Count -ne 2 -or !$input){throw 'Custom alert amount controls missing'}
   $checkBounds=$customChecks[$index].Current.BoundingRectangle
   $inputBounds=$input.Current.BoundingRectangle
   $gap=$inputBounds.Left-$checkBounds.Right
   if($gap -lt 0 -or $gap -gt 16 -or [Math]::Abs($inputBounds.Top-$checkBounds.Top) -gt 2){throw "Custom alert amount is not adjacent and aligned: $inputName gap=$gap"}
   Write-Host "PASS custom amount adjacent and aligned: $inputName checkbox=$checkBounds input=$inputBounds"
  }
  $mute=$nodes | Where-Object {$_.Current.Name -eq '通知静音' -and $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button} | Select-Object -First 1
  $pattern=$mute.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
  foreach($expectedMute in $true,$false) {
   $targetState=if($expectedMute){[Windows.Automation.ToggleState]::On}else{[Windows.Automation.ToggleState]::Off}
   if($pattern.Current.ToggleState -ne $targetState){$pattern.Toggle()}
   $saveDeadline=[datetime]::UtcNow.AddSeconds(5)
   do {
    Start-Sleep -Milliseconds 100
    $saved=Get-Content -LiteralPath $fixtureFile -Raw | ConvertFrom-Json
   } while($saved.notificationMuted -ne $expectedMute -and [datetime]::UtcNow -lt $saveDeadline)
   if($saved.notificationMuted -ne $expectedMute){throw "Mute toggle was not persisted: expected=$expectedMute"}
  }
  Write-Host 'PASS reminder controls, all thresholds visible, mute enabled and disabled persist to isolated settings'
 }
 if ($Page -eq 'Model') {
  $add=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'ModelAddProviderButton'))
  if(!$add -or $add.Current.Name -ne '添加提供商'){throw 'Model page add-provider action missing'}
  $refresh=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'ModelRefreshButton'))
  if(!$refresh -or $refresh.Current.Name -ne '刷新目录' -or $refresh.Current.IsOffscreen){throw 'Model catalog refresh action missing'}
  foreach($text in @('模型提供商','排列提供商顺序，管理其模型目录；自定义提供商可编辑、检测连通或删除。','管理由此启动器启动的 DSH 的模型提供商与模型目录。这里的设置只影响通过此启动器启动的 DSH。')) {
   if(!$rows.Where({$_.Name -eq $text}).Count){throw "Model page section missing: $text"}
  }
  foreach($text in @('DeepSeek','OpenAI','Anthropic','更多','编辑')) {
   if(!$rows.Where({$_.Name -eq $text}).Count){throw "Model provider fixture missing: $text"}
  }
  foreach($text in @('默认模型','同步到网页会话','应用模型','刷新会话')) {
   if($rows.Where({$_.Name -eq $text -and !$_.Offscreen}).Count){throw "Removed model action is still visible: $text"}
  }
  foreach($text in @('上移 DeepSeek','下移 DeepSeek')) {
   if(!$rows.Where({$_.Name -eq $text}).Count){throw "Model provider order control missing: $text"}
  }
  Write-Host 'PASS isolated model page fixture shows provider management, refresh, edit, more, and ordering without default-model or web-session controls'
 }
 if ($Page -in @('Patches','Updates')) {
  foreach ($tab in @('更新','补丁')) {
   if (!$rows.Where({$_.Name -eq $tab -and $_.Type -eq 'ControlType.TabItem' -and !$_.Offscreen}).Count) { throw "Tab missing: $tab" }
  }
 }
 if ($Page -eq 'ServerMetrics') {
  foreach ($text in @('服务器','CPU','内存','网络','硬盘','在线人数 · 近 24 小时','网络上传带宽')) {
   if (!$rows.Where({$_.Name -eq $text}).Count) { throw "Server panel missing: $text" }
  }
  if (!$rows.Where({$_.Name -eq '实时数据 · 每 2 秒更新'}).Count) { throw 'Live metrics not loaded' }
  if ($rows.Where({$_.Name -match '暂时无法读取在线人数历史'}).Count) { throw 'Live presence history failed' }
 }
 if ($Page -eq 'Patches') {
  foreach ($text in @('检查补丁更新','已安装补丁','可用补丁')) {
   if (!$rows.Where({$_.Name -eq $text}).Count) { throw "Patch management missing: $text" }
  }
 }
 if ($Page -eq 'Api' -and $SearchQuery) {
  $adminBox = $element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'AdminTokenBox'))
  if (!$adminBox -or $adminBox.Current.IsOffscreen) { throw 'Admin token search did not reveal its control' }
 }
 $rows | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $Output "$Page-uia.json") -Encoding utf8
 $handle = [IntPtr]::new($element.Current.NativeWindowHandle)
 $r = [SettingsCapture+RECT]::new()
 [SettingsCapture]::GetWindowRect($handle,[ref]$r) | Out-Null
 function Save-SettingsScreenshot([string]$Name) {
  $bitmap = [Drawing.Bitmap]::new($r.Right-$r.Left,$r.Bottom-$r.Top)
  $graphics = [Drawing.Graphics]::FromImage($bitmap)
  $dc = $graphics.GetHdc()
  try { if (![SettingsCapture]::PrintWindow($handle,$dc,2)) { throw 'Settings capture failed' } }
  finally { $graphics.ReleaseHdc($dc) }
  $bitmap.Save((Join-Path $Output "$Name.png"),[Drawing.Imaging.ImageFormat]::Png)
  $graphics.Dispose(); $bitmap.Dispose()
 }
 Save-SettingsScreenshot $Page
 if ($Page -eq 'Feedback') {
  function Feedback-Control([string]$Id) {
   $control=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
   if(!$control -and $Id -in @('FeedbackPreviousPageButton','FeedbackNextPageButton','FeedbackPageText','FeedbackCountText')) {
    # Some compiled WinUI peers expose the visible accessible name without x:Name as AutomationId.
    $expectedType=if($Id -in @('FeedbackPreviousPageButton','FeedbackNextPageButton')){[Windows.Automation.ControlType]::Button}else{[Windows.Automation.ControlType]::Text}
    $expectedName=switch($Id){'FeedbackPreviousPageButton'{'^上一页$'};'FeedbackNextPageButton'{'^下一页$'};'FeedbackPageText'{'^第 \d+(?: / \d+)? 页$'};'FeedbackCountText'{'^每页 20 条(?: · 共 \d+ 条)?$'}}
    $candidates=@($element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$expectedType)) | Where-Object {!$_.Current.IsOffscreen -and $_.Current.Name -match $expectedName})
    if($candidates.Count -eq 1){$control=$candidates[0]}
   }
   if(!$control){throw "Feedback control missing: $Id"}
   return $control
  }
  function Feedback-Tab([string]$HostId,[string]$Title) {
   $hostControl=Feedback-Control $HostId
   $tab=$hostControl.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new([Windows.Automation.Condition[]]@([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Title),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TabItem))))
   if(!$tab -or $tab.Current.IsOffscreen){throw "Feedback tab not visible: $Title"}
   return $tab
  }
  $loadDeadline=[datetime]::UtcNow.AddSeconds(20)
  while(!(Feedback-Control 'FeedbackRefreshButton').Current.IsEnabled -and [datetime]::UtcNow -lt $loadDeadline) { Start-Sleep -Milliseconds 250 }
  if(!(Feedback-Control 'FeedbackRefreshButton').Current.IsEnabled){throw 'Live feedback list never finished loading'}
  $loadFailure=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'反馈暂时不可用'))
  if($loadFailure -and !$loadFailure.Current.IsOffscreen){throw 'Live feedback list failed to load'}
  $feedbackTab=Feedback-Tab 'AboutTabs' '反馈与建议'
  if(!$feedbackTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected){throw 'Feedback is not selected as an About subpage'}
  $initialScope=if($SearchQuery -in @('我的提交','wdtj')){'我的提交'}else{'全部反馈'}
  $scopeCombo=Feedback-Control 'FeedbackScopePivot'
  if($scopeCombo.Current.ControlType -ne [Windows.Automation.ControlType]::ComboBox -or $scopeCombo.Current.IsOffscreen){throw 'Feedback scope must be a visible native ComboBox'}
  $selectedScope=@($scopeCombo.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection())
  if($selectedScope.Count -ne 1 -or $selectedScope[0].Current.Name -ne $initialScope){throw "Unexpected initial feedback scope: expected '$initialScope', selected count=$($selectedScope.Count), names='$(@($selectedScope | ForEach-Object {$_.Current.Name}) -join '|')'"}
  $scopeCombo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Start-Sleep -Milliseconds 300
  function Feedback-ScopeItems {
   $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))
   foreach($window in $windows) {
    foreach($node in $window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)) {
     $selection=$null
     if(!$node.Current.IsOffscreen -and $node.Current.Name -in @('全部反馈','需求建议','漏洞反馈','我的提交') -and $node.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selection)) {$node}
    }
   }
  }
  $scopeItems=@(Feedback-ScopeItems | Sort-Object {$_.Current.BoundingRectangle.Top})
  $scopeNames=@($scopeItems | ForEach-Object {$_.Current.Name} | Select-Object -Unique)
  if(($scopeNames -join '|') -ne '全部反馈|需求建议|漏洞反馈|我的提交'){throw ('Unexpected feedback scope choices: '+($scopeNames -join '|'))}
  $scopeCombo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
  Start-Sleep -Milliseconds 300
  foreach($id in @('FeedbackPreviousPageButton','FeedbackNextPageButton','FeedbackPageText','FeedbackCountText')) {
   if((Feedback-Control $id).Current.IsOffscreen){throw "Feedback pagination is not visible: $id"}
  }
  if((Feedback-Control 'FeedbackCountText').Current.Name -notmatch '^每页 20 条'){throw 'Feedback page size label is missing'}
  if((Feedback-Control 'FeedbackPageText').Current.Name -notmatch '^第 1(?: / \d+)? 页$'){throw 'Initial feedback page number is not one'}
  if((Feedback-Control 'FeedbackPreviousPageButton').Current.IsEnabled){throw 'First feedback page unexpectedly enables previous page'}
  $loadingRing=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'FeedbackLoadingRing'))
  if($loadingRing -and !$loadingRing.Current.IsOffscreen){throw 'Idle feedback loading ring still consumes visible space'}
  $fab=Feedback-Control 'FeedbackAddButton'
  $fabBounds=$fab.Current.BoundingRectangle
  if($fab.Current.IsOffscreen -or $fab.Current.Name -ne '提交反馈' -or $fabBounds.Width -lt 45 -or $fabBounds.Height -lt 45 -or $fabBounds.Right -gt $r.Right -or $fabBounds.Bottom -gt $r.Bottom){throw 'Feedback add button is not visible within the window'}
  $fab.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 600
  $dialogTitle=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'提交反馈与建议'))
  if(!$dialogTitle -or $dialogTitle.Current.IsOffscreen){throw 'Feedback compose dialog did not open'}
  Save-SettingsScreenshot 'Feedback-compose'
  $cancel=$element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'取消')) | Where-Object {!$_.Current.IsOffscreen -and $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button} | Select-Object -First 1
  if(!$cancel){throw 'Feedback compose cancel button missing'}
  $cancel.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 500
  (Feedback-Tab 'AboutTabs' '关于').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 400
  $hiddenFab=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'FeedbackAddButton'))
  if($hiddenFab -and !$hiddenFab.Current.IsOffscreen){throw 'Feedback add button leaked onto About page'}
  (Feedback-Tab 'AboutTabs' '反馈与建议').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 500
  if((Feedback-Control 'FeedbackAddButton').Current.IsOffscreen){throw 'Feedback add button did not return with Feedback page'}
  Save-SettingsScreenshot 'Feedback-reopened'
  Write-Host 'PASS About/Feedback subpage, native scope choices, twenty-record pagination, collapsed idle loading, floating action, compose/cancel, and subpage switching without remote writes'
 }
 if ($SourceChoices) {
  $advanced=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'AcceleratorAdvancedToggle'))
  $advanced.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 300
  foreach($id in @('AcceleratorGhproxyRadio','AcceleratorGhProxyRadio','AcceleratorGhfastRadio','AcceleratorJsdelivrRadio')) {
   $mirror=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id))
   if(!$mirror -or [string]::IsNullOrWhiteSpace($mirror.Current.Name)){throw "Mirror has no initial label: $id"}
  }
  if($element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'AcceleratorBackendRadio'))){throw 'Backend must only be in the main source combo'}
  foreach($node in $nodes) {
   $scrollPattern=$null
   if($node.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$scrollPattern) -and $scrollPattern.Current.VerticallyScrollable) {$scrollPattern.SetScrollPercent(-1,25); break}
  }
  Start-Sleep -Milliseconds 300
  Save-SettingsScreenshot 'CDN-advanced'
  $combo=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'UpdateSourceComboBox'))
  if(!$combo){throw 'Online engine source combo missing'}
  $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Start-Sleep -Milliseconds 400
  function Source-Items {
   $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))
   foreach($window in $windows) {
    $all=$window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    foreach($node in $all) {
     $selectionPattern=$null
     if(!$node.Current.IsOffscreen -and $node.Current.Name -in @('大陆 CDN 加速','后端服务器加速','自定义','官方源') -and $node.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selectionPattern)) {$node}
    }
   }
  }
  $items=@(Source-Items | Sort-Object {$_.Current.BoundingRectangle.Top})
  $names=@($items | ForEach-Object {$_.Current.Name} | Select-Object -Unique)
  if(($names -join '|') -ne '大陆 CDN 加速|后端服务器加速|自定义|官方源'){throw ('Unexpected source choices: '+($names -join '|'))}
  Save-SettingsScreenshot 'Source-choices'
  function Selected-ComboName($control) {
   $selection=$null
   if($control.TryGetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern,[ref]$selection)) {
    $selected=@($selection.Current.GetSelection())
    if($selected.Count -eq 1) { return $selected[0].Current.Name }
   }
   return $control.Current.Name
  }
  function Select-Source([string]$selection) {
   $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
   Start-Sleep -Milliseconds 200
   $item=Source-Items | Where-Object {$_.Current.Name -eq $selection} | Select-Object -First 1
   if(!$item) { throw "Source item is not visible: $selection" }
   $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
   Start-Sleep -Milliseconds 500
  }
  function Read-IsolatedSettings {
   return Get-Content (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Raw | ConvertFrom-Json
  }
  $sourceEvidence=[Collections.Generic.List[object]]::new()
  function Assert-PluginSourceLabel([string]$expected) {
   $pluginCombo=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'PluginSourceComboBox'))
   if(!$pluginCombo){throw 'Plugin source ComboBox missing'}
   $pluginCombo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
   Start-Sleep -Milliseconds 200
   $pluginNames=@(foreach($window in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id))) {
    foreach($node in $window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)) {
     $selectionPattern=$null
     if(!$node.Current.IsOffscreen -and $node.Current.Name -in @('GitHub 官方','GitHub 大陆节点') -and $node.TryGetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern,[ref]$selectionPattern)) {$node.Current.Name}
    }
   })
   $pluginCombo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
   if($pluginNames -notcontains $expected){throw "Plugin source did not immediately follow online engine: expected=$expected actual=$($pluginNames -join '|')"}
   $saved=Read-IsolatedSettings
   $sourceEvidence.Add([pscustomobject]@{ProcessId=$process.Id;Engine=(Selected-ComboName $combo);UpdateSource=$saved.updateSource;MirrorSource=$saved.mirrorSource;PluginSource=$saved.pluginSource;GitHubLabel=$expected})
  }
  foreach($selection in @('后端服务器加速','官方源','大陆 CDN 加速','后端服务器加速')) {
   Select-Source $selection
   $settings=Read-IsolatedSettings
   if($selection -eq '官方源') {if($settings.updateSource -ne 'Official'){throw 'Official source failed to persist'}}
   elseif($selection -eq '后端服务器加速') {if($settings.updateSource -ne 'Accelerated' -or $settings.mirrorSource -ne 'backend'){throw 'Backend source failed to persist'}}
   elseif($settings.updateSource -ne 'Accelerated' -or $settings.mirrorSource -ne 'Auto'){throw 'CDN source failed to persist'}
   Assert-PluginSourceLabel $(if($selection -eq '官方源'){'GitHub 官方'}else{'GitHub 大陆节点'})
  }
  $pidBefore=$process.Id
  Select-Source '大陆 CDN 加速'
  $advancedToggle=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'AcceleratorAdvancedToggle'))
  if(!$advancedToggle -or $advancedToggle.Current.IsOffscreen){throw 'Advanced accelerator toggle did not return for CDN source'}
  $advancedToggle.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 300
  $customRadio=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'AcceleratorGhproxyRadio'))
  if(!$customRadio -or $customRadio.Current.IsOffscreen){throw 'ghproxy accelerator radio is not visible'}
  $customRadio.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 500
  $settings=Read-IsolatedSettings
  if($settings.updateSource -ne 'Accelerated' -or $settings.mirrorSource -ne 'ghproxy'){throw 'Custom accelerator radio failed to persist immediately'}
  $combo=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'UpdateSourceComboBox'))
  if((Selected-ComboName $combo) -ne '自定义'){throw "Custom radio did not update online engine selection: $(Selected-ComboName $combo)"}
  Assert-PluginSourceLabel 'GitHub 大陆节点'
  Save-SettingsScreenshot 'Source-custom-ghproxy'
  Select-Source '后端服务器加速'
  $settings=Read-IsolatedSettings
  if($settings.updateSource -ne 'Accelerated' -or $settings.mirrorSource -ne 'backend'){throw 'Custom to backend transition failed'}
  if($advancedToggle -and !$advancedToggle.Current.IsOffscreen){throw 'Advanced accelerator controls remained visible for backend source'}
  Assert-PluginSourceLabel 'GitHub 大陆节点'
  Save-SettingsScreenshot 'Source-backend-selected'
  Select-Source '自定义'
  $settings=Read-IsolatedSettings
  if($settings.updateSource -ne 'Accelerated' -or $settings.mirrorSource -ne 'Auto'){throw 'Backend to custom transition retained backend marker'}
  if((Selected-ComboName $combo) -ne '大陆 CDN 加速'){throw "Backend to custom transition did not normalize to CDN: $(Selected-ComboName $combo)"}
  Assert-PluginSourceLabel 'GitHub 大陆节点'
  $process.Refresh()
  if($process.HasExited -or $process.Id -ne $pidBefore){throw 'Online engine switching restarted or exited the settings process'}
  Write-Host 'PASS live UIA accelerator radio selection, custom/backend transitions, normalized backend-to-custom fallback, persistence, and same-process continuity'
  $sourceEvidence | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Output 'Source-switch-evidence.json') -Encoding utf8
  Save-SettingsScreenshot 'Source-normalized-CDN'
 }
 if ($ExtensionTabs) {
  function Extension-Control([string]$Id) {
   $control=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
   if(!$control){throw "Extension navigation control missing: $Id"}
   return $control
  }
  function Select-ExtensionControl([string]$Id) {
   (Extension-Control $Id).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
   Start-Sleep -Milliseconds 350
  }
  function Select-ExtensionPage([string]$Title) {
   $pivot=Extension-Control 'FeaturesTabs'
   $tab=$pivot.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new([Windows.Automation.Condition[]]@([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Title),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TabItem))))
   if(!$tab){throw "Extension page tab missing: $Title"}
   $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
   Start-Sleep -Milliseconds 350
  }
  function Assert-ExtensionSelected([string]$Id) {
   $control=Extension-Control $Id
   if(!$control.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected){throw "Extension selection reset: $Id"}
  }
  Select-ExtensionPage '插件'
  Assert-ExtensionSelected 'FeaturedPluginsTab'
  Save-SettingsScreenshot 'Plugins-default-featured'
  Select-ExtensionPage '技能'
  Assert-ExtensionSelected 'FeaturedSkillsTab'
  Save-SettingsScreenshot 'Skills-default-featured'
  foreach($choice in @(@{Page='插件';Other='技能';Id='LocalPluginsTab'},@{Page='插件';Other='技能';Id='OnlinePluginsTab'},@{Page='技能';Other='插件';Id='LocalSkillsTab'},@{Page='技能';Other='插件';Id='MarketSkillsTab'})) {
   Select-ExtensionPage $choice.Page
   Select-ExtensionControl $choice.Id
   Select-ExtensionPage $choice.Other
   Select-ExtensionPage $choice.Page
   Assert-ExtensionSelected $choice.Id
   Select-ExtensionControl 'HomeNavItem'
   Select-ExtensionControl 'FeaturesNavItem'
   Assert-ExtensionSelected $choice.Id
   Save-SettingsScreenshot ($choice.Id+'-retained')
  }
  Write-Host 'PASS plugin/skill featured defaults and four inner-tab choices survive page and navigation re-entry'
 }
 if ($MessageEditors) {
  function Find-Id([string]$Id) {
   return $element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
  }
  function Click-Id([string]$Id) {
   $control=Find-Id $Id
   if (!$control) { throw "Missing control: $Id" }
   $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
   Start-Sleep -Milliseconds 500
  }
  $modules=Find-Id 'DeveloperModuleList'
  $items=$modules.FindAll([Windows.Automation.TreeScope]::Children,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
  $items[1].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 500
  $compose=Find-Id 'DeveloperAnnouncementComposeButton'
  $push=Find-Id 'DeveloperAnnouncementPushButton'
  $refresh=Find-Id 'DeveloperMessageRefreshButton'
  if(!$compose -or !$push -or !$refresh -or $compose.Current.IsOffscreen -or $push.Current.IsOffscreen -or $refresh.Current.IsOffscreen) { throw 'Announcement management toolbar is incomplete' }
  $composeBounds=$compose.Current.BoundingRectangle
  $pushBounds=$push.Current.BoundingRectangle
  if($push.Current.Name -ne '推送公告' -or $pushBounds.Left -lt $composeBounds.Right -or [Math]::Abs($pushBounds.Top-$composeBounds.Top) -gt 4) { throw 'Push announcement is not on the right of compose in the same row' }
  $fixture=Get-Content -LiteralPath $fixtureFile -Raw | ConvertFrom-Json
  if($fixture.adminTokenProtected -or $fixture.gitHubTokenProtected) { throw 'Message smoke requires empty fixture credentials' }
  Click-Id 'DeveloperAnnouncementPushButton'
  $missingToken=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Token 未填写'))
  if(!$missingToken -or $missingToken.Current.IsOffscreen) { throw 'Push announcement did not retain the credential guard' }
  Save-SettingsScreenshot 'Announcement-management'
  Write-Host 'PASS announcement toolbar position and missing-token guard without a remote write'
  $capturedManagement=@{}
  foreach ($kind in @('Announcement','Notification','Announcement','Notification')) {
   $views=Find-Id 'DeveloperMessageViews'
   $tabs=$views.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::TabItem))
   $tabIndex=if($kind -eq 'Announcement'){0}else{1}
   $tabs[$tabIndex].GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
   Start-Sleep -Milliseconds 400
   if(!$capturedManagement[$kind]) {
    if($kind -eq 'Notification') {
     foreach($id in @('DeveloperNotificationComposeButton','DeveloperNotificationLoadButton','DeveloperNotificationStopButton','DeveloperNotificationMetricsButton')) {
      $control=Find-Id $id
      if(!$control) { throw "Notification management action missing: $id" }
     }
    }
    Save-SettingsScreenshot "$kind-management"
    $capturedManagement[$kind]=$true
   }
   Click-Id "Developer${kind}ComposeButton"
   $process.Refresh()
   if($process.HasExited) { throw 'Compose editor crashed application' }
   $title=Find-Id 'DeveloperMessageTitleBox'
   $body=Find-Id 'DeveloperMessageBodyBox'
   if(!$title -or !$body -or $title.Current.IsOffscreen -or $body.Current.IsOffscreen) { throw "Editor incomplete: $kind" }
   Save-SettingsScreenshot "Editor-$kind"
   $body.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('Cancelled draft must not persist')
   $cancel=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new([Windows.Automation.Condition[]]@([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'取消'),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button))))
   if(!$cancel) { throw 'Editor cancel button missing' }
   $cancel.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
   Start-Sleep -Milliseconds 500
   $process.Refresh()
   if($process.HasExited) { throw 'Cancel crashed application' }
  }
  Save-SettingsScreenshot 'Notification-management'
  $managementScroll=$null
  foreach($node in $element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)) {
   $scrollPattern=$null
   if($node.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$scrollPattern) -and $scrollPattern.Current.VerticallyScrollable) { $managementScroll=$scrollPattern; break }
  }
  if($managementScroll) {
   $managementScroll.SetScrollPercent(-1,100)
   Start-Sleep -Milliseconds 400
   $metricsButton=Find-Id 'DeveloperNotificationMetricsButton'
   if(!$metricsButton -or $metricsButton.Current.IsOffscreen) { throw 'Notification metrics cannot be reached by scrolling' }
   Save-SettingsScreenshot 'Notification-management-metrics'
  }
  Write-Host 'PASS announcement/notification compose, cancel and reopen four times'
 }
 if ($Page -eq 'ServerMetrics') {
  $scroll = $null
  foreach ($node in $nodes) {
   $pattern = $null
   if ($node.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$pattern) -and $pattern.Current.VerticallyScrollable) { $scroll=$pattern; break }
  }
  if (!$scroll) { throw 'Server metrics scroll surface missing' }
  foreach ($percent in @(50,100)) {
   $scroll.SetScrollPercent(-1,$percent)
   Start-Sleep -Milliseconds 500
   Save-SettingsScreenshot "$Page-scroll-$percent"
  }
 }
 $rows | Format-Table -AutoSize
 Write-Host "PASS settings UI: $Page"
} finally { if (!$process.HasExited) { $process.CloseMainWindow() | Out-Null; if (!$process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force -ErrorAction Continue } } }
