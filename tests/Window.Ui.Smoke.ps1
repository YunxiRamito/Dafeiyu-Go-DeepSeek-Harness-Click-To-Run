param([Parameter(Mandatory)][string]$Dist, [Parameter(Mandatory)][string]$Output,
      [Parameter(Mandatory)][string]$ServiceLog, [int]$BackendProcessId,
      [ValidateSet('Light','Dark')][string[]]$Themes = @('Light','Dark','Light'), [switch]$VerifyWebAccessibility)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
if (-not $Output.StartsWith('G:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an isolated G: output directory.' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowSmokeNative {
 public delegate bool WindowCallback(IntPtr h, IntPtr parameter);
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, WindowCallback callback, IntPtr parameter);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int count);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr processId);
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint source, uint target, bool attach);
 [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint message, IntPtr wparam, IntPtr lparam);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int width, int height, bool repaint);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
 [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attribute, out int value, int size);
}
'@
[WindowSmokeNative]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$url = [regex]::Match((Get-Content -LiteralPath $ServiceLog -Raw), 'http://127\.0\.0\.1:\d+/\?token=[^\s]+').Value
if (-not $url) { throw 'Isolated backend did not provide an authenticated URL.' }
$env:DAFEIYU_WINDOW_PREVIEW_URL = $url
$env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY = Join-Path $Output 'settings'
$env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY = Join-Path $Output 'downloads'
$env:TEMP = Join-Path $Output 'tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force -Path $env:TEMP,$env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY | Out-Null
$rounds = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
function Check($value, $label) {
 $checks.Add(@{passed=[bool]$value;label=$label})
 ('{0:O} {1}: {2}' -f [datetime]::UtcNow,[bool]$value,$label) | Add-Content -LiteralPath (Join-Path $Output 'steps.log')
 if (-not $value) { throw $label }
}
function Find-ShellId($window, [string]$id) {
 $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
 $element = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition)
 if ($null -eq $element) { $element = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Descendants,$condition) }
 return $element
}
function Invoke-ShellId($window, [string]$id) {
 $element = Find-ShellId $window $id
 Check ($null -ne $element -and $element.Current.IsEnabled -and -not $element.Current.IsOffscreen) "visible enabled shell command: $id"
 $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Start-Sleep -Milliseconds 200
 return $element
}
function Confirm-Foreground($window, [int]$round) {
 $handle = [IntPtr]::new($window.Current.NativeWindowHandle)
 $deadline = [datetime]::UtcNow.AddSeconds(5)
 do {
  $currentThread = [WindowSmokeNative]::GetCurrentThreadId()
  $foregroundThread = [WindowSmokeNative]::GetWindowThreadProcessId([WindowSmokeNative]::GetForegroundWindow(),[IntPtr]::Zero)
  $attached = $foregroundThread -ne 0 -and $foregroundThread -ne $currentThread -and
    [WindowSmokeNative]::AttachThreadInput($currentThread,$foregroundThread,$true)
  try {
   [WindowSmokeNative]::BringWindowToTop($handle) | Out-Null
   [WindowSmokeNative]::SetForegroundWindow($handle) | Out-Null
  } finally { if ($attached) { [WindowSmokeNative]::AttachThreadInput($currentThread,$foregroundThread,$false) | Out-Null } }
  Start-Sleep -Milliseconds 150
 } while ([WindowSmokeNative]::GetForegroundWindow() -ne $handle -and [datetime]::UtcNow -lt $deadline)
 Check ([WindowSmokeNative]::GetForegroundWindow() -eq $handle) "round ${round}: actual host HWND is foreground"
}
function Invoke-Continue($element, $window, [int]$round) {
 $current = $element.Current
 $rectangle = $current.BoundingRectangle
 $bounds = $window.Current.BoundingRectangle
 Check ($current.Name -in @('继续','稍后配置') -and $current.IsEnabled -and -not $current.IsOffscreen -and
   $current.ControlType -eq [Windows.Automation.ControlType]::Button -and
   $rectangle.Width -gt 0 -and $rectangle.Height -gt 0 -and
   $rectangle.Left -ge $bounds.Left -and $rectangle.Top -ge $bounds.Top -and
   $rectangle.Right -le $bounds.Right -and $rectangle.Bottom -le $bounds.Bottom) "round ${round}: safe dismiss name and visible button bounds are verified"
 $pattern = $null
 if ($element.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
  ([Windows.Automation.InvokePattern]$pattern).Invoke()
  return
 }
 $oldCursor = [Windows.Forms.Cursor]::Position
 try {
  Confirm-Foreground $window $round
  $element.SetFocus()
  Check ([WindowSmokeNative]::SetCursorPos([int]($rectangle.Left + $rectangle.Width / 2),
      [int]($rectangle.Top + $rectangle.Height / 2))) "round ${round}: pointer targets verified continue bounds"
  [WindowSmokeNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
  [WindowSmokeNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
 } finally { [Windows.Forms.Cursor]::Position = $oldCursor }
}
function Find-WebElement($window, $condition) {
 $handles = [Collections.Generic.List[IntPtr]]::new()
 [WindowSmokeNative]::EnumChildWindows([IntPtr]::new($window.Current.NativeWindowHandle),
   [WindowSmokeNative+WindowCallback]{ param($handle, $unused) $handles.Add($handle); return $true }, [IntPtr]::Zero) | Out-Null
 foreach ($handle in $handles) {
  $className = [Text.StringBuilder]::new(256)
  [void][WindowSmokeNative]::GetClassName($handle, $className, $className.Capacity)
  if ($className.ToString() -ne 'InputSiteWindowClass') { continue }
  try {
   $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
   $found = $root.FindFirst([Windows.Automation.TreeScope]::Subtree, $condition)
   if ($null -ne $found) { return $found }
  } catch [Windows.Automation.ElementNotAvailableException] { }
 }
 return $null
}
function Dismiss-KeySetup($window, [int]$round) {
 $condition = [Windows.Automation.AndCondition]::new(
   [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '稍后配置'),
   [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
     [Windows.Automation.ControlType]::Button))
 $later = Find-WebElement $window $condition
 if ($null -ne $later) {
  Invoke-Continue $later $window $round
  $deadline = [datetime]::UtcNow.AddSeconds(10)
  do { Start-Sleep -Milliseconds 250; $later = Find-WebElement $window $condition }
  while ($null -ne $later -and [datetime]::UtcNow -lt $deadline)
 }
 Check ($null -eq $later) "round ${round}: API key setup is dismissed without entering credentials or submitting a request"
}
function Confirm-Composer($window, [int]$round) {
 $handles = [Collections.Generic.List[IntPtr]]::new()
 [WindowSmokeNative]::EnumChildWindows([IntPtr]::new($window.Current.NativeWindowHandle),
   [WindowSmokeNative+WindowCallback]{ param($handle, $unused) $handles.Add($handle); return $true }, [IntPtr]::Zero) | Out-Null
 $edits = @()
 foreach ($handle in $handles) {
  $className = [Text.StringBuilder]::new(256)
  [void][WindowSmokeNative]::GetClassName($handle, $className, $className.Capacity)
  if ($className.ToString() -ne 'InputSiteWindowClass') { continue }
  $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
  $condition = [Windows.Automation.PropertyCondition]::new(
    [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Edit)
  $edits = @($root.FindAll([Windows.Automation.TreeScope]::Descendants, $condition) |
    Where-Object { $_.Current.IsEnabled -and -not $_.Current.IsOffscreen -and $_.Current.IsKeyboardFocusable })
  if ($edits.Count) { break }
 }
 $apiTitle = [Windows.Automation.PropertyCondition]::new(
   [Windows.Automation.AutomationElement]::NameProperty, '添加一个 API Key 开始使用')
 Check ($null -eq (Find-WebElement $window $apiTitle)) "round ${round}: API key modal title is absent"
 Check ($edits.Count -eq 1) "round ${round}: exactly one visible web edit identifies the chat composer"
 $input = $edits[0].Current
 Check (($input.Name + ' ' + $input.HelpText) -notmatch '(?i)API.?Key|API 密钥' -and
   $input.BoundingRectangle.Width -gt $window.Current.BoundingRectangle.Width * 0.5) "round ${round}: composer identity and width exclude a credentials field"
 return @{name=$input.Name;helpText=$input.HelpText;className=$input.ClassName;
   automationId=$input.AutomationId;controlType=$input.ControlType.ProgrammaticName;
   width=$input.BoundingRectangle.Width;height=$input.BoundingRectangle.Height;
   apiModalAbsent=$true;visibleEditCount=$edits.Count}
}
function Snapshot([int]$rootProcessId) {
 $all = @(Get-CimInstance Win32_Process)
 $ids = [Collections.Generic.HashSet[int]]::new()
 [void]$ids.Add($rootProcessId)
 do {
  $changed = $false
  foreach ($entry in $all) { if ($ids.Contains([int]$entry.ParentProcessId) -and $ids.Add([int]$entry.ProcessId)) { $changed = $true } }
 } while ($changed)
 $privateWorking = @{}
 Get-CimInstance Win32_PerfRawData_PerfProc_Process | ForEach-Object { $privateWorking[[int]$_.IDProcess] = [long]$_.WorkingSetPrivate }
 $result = @($all | Where-Object { $ids.Contains([int]$_.ProcessId) } | ForEach-Object {
  $current = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
  if ($current) { @{id=$current.Id;parent=[int]$_.ParentProcessId;name=$current.ProcessName;
    privateBytes=$current.PrivateMemorySize64;privateWorkingSet=$privateWorking[$current.Id];workingSet=$current.WorkingSet64} }
 })
 return @{processes=$result;privateBytes=($result | Measure-Object privateBytes -Sum).Sum;
   privateWorkingSet=($result | Measure-Object privateWorkingSet -Sum).Sum}
}
try {
 for ($round = 1; $round -le 3; $round++) {
  $theme = $Themes[($round - 1) % $Themes.Count]
  $material = @('Mica','MicaAlt','Solid')[$round - 1]
  $windowStyle = if ($round -eq 2) { 'Windows10' } else { 'Windows11' }
  @{theme=$theme;material=$material;windowStyle=$windowStyle;pageOpenMode='Window';legacyApiKeyMigrationCompleted=$true;
    dshRoot=(Join-Path $Output 'uninstalled-dsh');notificationMuted=$true} | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Encoding utf8
  $diagnostics = Join-Path $Output "page-$round.json"
  $env:DAFEIYU_WINDOW_QA_OUTPUT = $diagnostics
  $env:__COMPAT_LAYER = 'RunAsInvoker'
  Remove-Item -LiteralPath $diagnostics -Force -ErrorAction SilentlyContinue
  $previousBrowserArguments = [Environment]::GetEnvironmentVariable('WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS', 'Process')
  try {
   $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = (($previousBrowserArguments + ' --force-renderer-accessibility').Trim())
   $previewMode = if ($round -eq 1) { '--settings-preview=ApplicationStartup' } else { '--settings-preview=ApplicationWindow' }
   $process = Start-Process -FilePath (Join-Path $Dist 'DeepSeek Harness.Core.exe') -ArgumentList $previewMode -WindowStyle Normal -PassThru
  } finally {
   [Environment]::SetEnvironmentVariable('WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS', $previousBrowserArguments, 'Process')
   Remove-Item Env:__COMPAT_LAYER
  }
  try {
   if ($round -eq 1) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
     $startupWindow = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
     if ($null -eq $startupWindow) { Start-Sleep -Milliseconds 100 }
    } while ($null -eq $startupWindow -and [datetime]::UtcNow -lt $deadline)
    Check ($null -ne $startupWindow) 'native startup shell appears before service-page navigation'
    Confirm-Foreground $startupWindow $round
    $deadline = [datetime]::UtcNow.AddSeconds(3)
    do {
     $brand = $startupWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
       [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Dafeiyu-GO'))
     $terminal = Find-ShellId $startupWindow 'DshWindow.StartupDetail'
     if ($null -eq $brand -or $brand.Current.IsOffscreen) { Start-Sleep -Milliseconds 100 }
    } while (($null -eq $brand -or $brand.Current.IsOffscreen) -and [datetime]::UtcNow -lt $deadline)
    Check ($null -ne $brand -and -not $brand.Current.IsOffscreen) 'central whale/brand startup layout is visible'
    Check ($null -ne $terminal -and -not $terminal.Current.IsOffscreen) 'bottom-left startup terminal is visible'
    $brandBounds = $brand.Current.BoundingRectangle
    $terminalBounds = $terminal.Current.BoundingRectangle
    Check ($terminalBounds.Top -ge $brandBounds.Bottom -or $terminalBounds.Right -le $brandBounds.Left) 'startup terminal and central brand do not overlap'
    $bounds = $startupWindow.Current.BoundingRectangle
    $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width,[int]$bounds.Height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
     $graphics.CopyFromScreen([int]$bounds.X,[int]$bounds.Y,0,0,$bitmap.Size)
     $bitmap.Save((Join-Path $Output 'startup-native.png'),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
   }
   $deadline = [datetime]::UtcNow.AddSeconds(45)
   $data = $null
   while ([datetime]::UtcNow -lt $deadline) {
    $process.Refresh()
    if ($process.HasExited) { throw "Window exited before ready: $($process.ExitCode)" }
    if (Test-Path -LiteralPath $diagnostics) {
     try { $data = Get-Content -LiteralPath $diagnostics -Raw | ConvertFrom-Json } catch { }
     if ($data.sample -eq 2) { break }
    }
    Start-Sleep -Milliseconds 250
   }
   Check ($data -and $data.sample -eq 2) "round ${round}: actual WebView2 navigation diagnostics"
   Check (-not $data.shell.startupVisible -and -not $data.shell.startupFailed) "round ${round}: actual startup overlay fades after real SPA readiness and late background progress"
   Check ($data.page.bodyTextLength -gt 30 -and $data.page.elementCount -gt 50) "round ${round}: actual DSH SPA rendered"
   Check ($data.page.hasWebSocket -and $data.page.hasWorker -and $data.page.hasGetUserMedia -and $data.page.hasAudioContext) "round ${round}: browser APIs available (not functional microphone or upload acceptance)"
   Check ($data.shell.customTitleBar -and $data.shell.titleBarHeight -eq 32 -and
     -not $data.shell.statusVisible) "round ${round}: single custom WinUI title bar without a permanent status toolbar"
   Check ($data.shell.theme -eq $theme -and $data.shell.dpiScale -gt 0) "round ${round}: requested $theme theme and actual DPI scale"
   $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)
   $deadline = [datetime]::UtcNow.AddSeconds(15)
   do {
    $window = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$condition)
    if ($null -eq $window) { Start-Sleep -Milliseconds 250 }
   } while ($null -eq $window -and [datetime]::UtcNow -lt $deadline)
   Check ($null -ne $window) "round ${round}: native window exists"
   $hostHandle = [IntPtr]::new($window.Current.NativeWindowHandle)
   $hostClass = [Text.StringBuilder]::new(256)
   [void][WindowSmokeNative]::GetClassName($hostHandle, $hostClass, $hostClass.Capacity)
   $process.Refresh()
   $moduleNames = @($process.Modules | ForEach-Object ModuleName)
   Check ($moduleNames -contains 'Microsoft.UI.Xaml.dll') "round ${round}: running host loads WinUI 3 Microsoft.UI.Xaml"
   Check (Test-Path -LiteralPath (Join-Path $env:SETTINGS_SMOKE_GUARD_DIRECTORY ('guard-loaded-' + $process.Id + '.txt'))) "round ${round}: HTTP pre-send guard loaded in the isolated window preview"
   $cornerPreference = 0
   $darkTitleBar = 0
   $cornerResult = [WindowSmokeNative]::DwmGetWindowAttribute($hostHandle, 33, [ref]$cornerPreference, 4)
   $titleBarResult = [WindowSmokeNative]::DwmGetWindowAttribute($hostHandle, 20, [ref]$darkTitleBar, 4)
   if ([Environment]::OSVersion.Version.Build -ge 22000) {
    Check ($cornerResult -eq 0 -and $cornerPreference -eq $(if ($windowStyle -eq 'Windows10') { 1 } else { 2 })) "round ${round}: native corners follow saved $windowStyle preference"
   }
   $hostEvidence = @{executable=$process.MainModule.FileName;windowClass=$hostClass.ToString();
     uiFramework='Microsoft.UI.Xaml.Window / WinUI 3';content='Microsoft.UI.Xaml.Controls.WebView2';
     loadedUiModules=@($moduleNames | Where-Object { $_ -match 'Microsoft\.UI\.Xaml|WebView2|PresentationFramework' });
     dwmCornerPreference=$cornerPreference;dwmCornerHResult=$cornerResult;
     dwmDarkTitleBar=$darkTitleBar;dwmTitleBarHResult=$titleBarResult;requestedMaterial=$material;requestedWindowStyle=$windowStyle}
   $windowPattern = $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
   [WindowSmokeNative]::SetForegroundWindow([IntPtr]::new($window.Current.NativeWindowHandle)) | Out-Null
   $composer = $null
   if ($VerifyWebAccessibility) {
   $previewTitleCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '预览版说明')
   $continueCondition = [Windows.Automation.AndCondition]::new(
     [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '继续'),
     [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
       [Windows.Automation.ControlType]::Button))
   $continue = $null
   $deadline = [datetime]::UtcNow.AddSeconds(15)
   do {
    $continue = Find-WebElement $window $continueCondition
    if ($null -eq $continue) { Start-Sleep -Milliseconds 250 }
   } while ($null -eq $continue -and [datetime]::UtcNow -lt $deadline)
   if ($round -eq 1 -and $null -ne $continue) {
    if ($null -eq $continue) {
     $nodes = $window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
     $nodes | ForEach-Object { '{0}: {1}' -f $_.Current.ControlType.ProgrammaticName, $_.Current.Name } |
       Set-Content -LiteralPath (Join-Path $Output "uia-$round.txt") -Encoding utf8
     $handles = [Collections.Generic.List[IntPtr]]::new()
     [WindowSmokeNative]::EnumChildWindows([IntPtr]::new($window.Current.NativeWindowHandle),
       [WindowSmokeNative+WindowCallback]{ param($handle, $unused) $handles.Add($handle); return $true }, [IntPtr]::Zero) | Out-Null
     foreach ($handle in $handles) {
      $className = [Text.StringBuilder]::new(256)
      [void][WindowSmokeNative]::GetClassName($handle, $className, $className.Capacity)
      $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
      $children = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
      ('HWND class {0}; UIA descendants {1}' -f $className.ToString(),$children.Count) |
        Add-Content -LiteralPath (Join-Path $Output "uia-$round.txt") -Encoding utf8
     }
    }
    Check ($null -ne $continue -and $continue.Current.IsEnabled) "round ${round}: preview notice continue action is available"
    $serviceLogBeforeContinue = Get-Content -LiteralPath $ServiceLog -Raw
    Invoke-Continue $continue $window $round
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
     Start-Sleep -Milliseconds 250
     $continueAfter = Find-WebElement $window $continueCondition
    } while ($null -ne $continueAfter -and [datetime]::UtcNow -lt $deadline)
    Check ($null -eq $continueAfter) "round ${round}: preview notice closes after UIA continue"
    $previewTitleAfter = Find-WebElement $window $previewTitleCondition
    Check ($null -eq $previewTitleAfter) "round ${round}: preview notice no longer covers the workspace"
   } else {
    Check ($null -eq $continue -and $null -eq (Find-WebElement $window $previewTitleCondition)) "round ${round}: current page has no preview notice; no Continue click claimed"
    $serviceLogBeforeContinue = Get-Content -LiteralPath $ServiceLog -Raw
   }
   Dismiss-KeySetup $window $round
   $composer = Confirm-Composer $window $round
   $editCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,
     [Windows.Automation.ControlType]::Edit)
   $deadline = [datetime]::UtcNow.AddSeconds(10)
   do {
    $workspaceInput = Find-WebElement $window $editCondition
     if ($null -ne $workspaceInput -and (-not $workspaceInput.Current.IsEnabled -or $workspaceInput.Current.IsOffscreen -or
         -not $workspaceInput.Current.IsKeyboardFocusable)) { $workspaceInput = $null }
    if ($null -eq $workspaceInput) { Start-Sleep -Milliseconds 250 }
   } while ($null -eq $workspaceInput -and [datetime]::UtcNow -lt $deadline)
   Check ($null -ne $workspaceInput) "round ${round}: workspace exposes a visible interactive input after continue"
   $workspaceRectangle = $workspaceInput.Current.BoundingRectangle
   Check ($workspaceRectangle.Width -gt 0 -and $workspaceRectangle.Height -gt 0 -and
     $workspaceInput.Current.IsKeyboardFocusable) "round ${round}: workspace input reports keyboard interaction without submitting a model request"
   $serviceLogAfterContinue = Get-Content -LiteralPath $ServiceLog -Raw
   $logDelta = if ($serviceLogAfterContinue.Length -ge $serviceLogBeforeContinue.Length) {
     $serviceLogAfterContinue.Substring($serviceLogBeforeContinue.Length)
   } else { $serviceLogAfterContinue }
   $paidRoute = [regex]::IsMatch($logDelta,
     '(?i)(/v1/chat/completions|/chat/completions|/api/chat|/api/generate|/api/inference|/billing|/payment|/checkout|/purchase)')
   Check (-not $paidRoute) "round ${round}: continue does not emit a paid model or purchase route marker"
   }
   $windowPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Minimized)
   Start-Sleep -Milliseconds 250
   Check ($windowPattern.Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Minimized) "round ${round}: native minimize"
   $windowPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal)
   [WindowSmokeNative]::SetForegroundWindow([IntPtr]::new($window.Current.NativeWindowHandle)) | Out-Null
   Start-Sleep -Milliseconds 500
   Check ($windowPattern.Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Normal) "round ${round}: native restore"
   $buttons = @('ApplicationMenu','EditMenu','Settings','CheckUpdates') | ForEach-Object { Find-ShellId $window ('DshWindow.' + $_) }
   Check (@($buttons | Where-Object { $null -ne $_ -and $_.Current.IsEnabled }).Count -eq 4) "round ${round}: all four compact menu entries are enabled"
   $shellBounds = $window.Current.BoundingRectangle
   $firstBounds = $buttons[0].Current.BoundingRectangle
   $lastBounds = $buttons[-1].Current.BoundingRectangle
   $appIcon = Find-ShellId $window 'DshWindow.AppIcon'
   Check ($null -ne $appIcon -and -not $appIcon.Current.IsOffscreen -and
     $appIcon.Current.BoundingRectangle.Width -gt 0 -and $appIcon.Current.BoundingRectangle.Right -le $firstBounds.Left) "round ${round}: Dafeiyu application icon is visible before the left menus"
   Check (@($buttons | Where-Object { [Math]::Abs($_.Current.BoundingRectangle.Top - $firstBounds.Top) -gt 1 -or
     [Math]::Abs($_.Current.BoundingRectangle.Height - $firstBounds.Height) -gt 1 }).Count -eq 0 -and
     $lastBounds.Right -lt $shellBounds.Left + $shellBounds.Width / 2) "round ${round}: equal-height menu buttons all occupy the left side"
   $dragX = [int]($shellBounds.Left + $shellBounds.Width / 2)
   $dragY = [int]($shellBounds.Top + 16 * $data.shell.dpiScale)
   $dragPoint = [IntPtr]::new((($dragY -band 65535) -shl 16) -bor ($dragX -band 65535))
   Check ([WindowSmokeNative]::SendMessage($hostHandle, 0x84, [IntPtr]::Zero, $dragPoint).ToInt64() -eq 2) "round ${round}: custom title background is a native caption drag region"
   $windowPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Maximized)
   Start-Sleep -Milliseconds 250
   Check ($windowPattern.Current.WindowVisualState -eq [Windows.Automation.WindowVisualState]::Maximized) "round ${round}: native maximize with custom title bar"
   $windowPattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal)
   Start-Sleep -Milliseconds 250
   $null = Invoke-ShellId $window 'DshWindow.ApplicationMenu'
   $reload = Find-ShellId $window 'DshWindow.Reload'
   $browser = Find-ShellId $window 'DshWindow.OpenBrowser'
   Check ($null -ne $reload -and $reload.Current.IsEnabled -and $null -ne $browser -and $browser.Current.IsEnabled) "round ${round}: application flyout exposes refresh and browser commands"
   Check ($null -ne (Find-ShellId $window 'DshWindow.Back') -and $null -ne (Find-ShellId $window 'DshWindow.Forward')) "round ${round}: history commands exist with native availability state"
   $previousCapture = [datetime]$data.capturedUtc
   $reload.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
   $deadline = [datetime]::UtcNow.AddSeconds(30)
   do {
    Start-Sleep -Milliseconds 250
    try { $reloaded = Get-Content -LiteralPath $diagnostics -Raw | ConvertFrom-Json } catch { $reloaded = $null }
   } while ((-not $reloaded -or $reloaded.sample -ne 2 -or [datetime]$reloaded.capturedUtc -le $previousCapture) -and [datetime]::UtcNow -lt $deadline)
   Check ($reloaded -and $reloaded.sample -eq 2 -and [datetime]$reloaded.capturedUtc -gt $previousCapture -and $reloaded.page.elementCount -gt 50) "round ${round}: reload renders actual DSH again"
   if ($VerifyWebAccessibility) {
   Check ($null -eq (Find-WebElement $window $continueCondition) -and
     $null -eq (Find-WebElement $window $previewTitleCondition)) "round ${round}: workspace stays unobstructed after reload"
   Dismiss-KeySetup $window $round
   $composer = Confirm-Composer $window $round
   }
   $data = $reloaded
   if ($round -eq 1) {
    $previousCapture = [datetime]$data.capturedUtc
    $null = Invoke-ShellId $window 'DshWindow.ApplicationMenu'
    $null = Invoke-ShellId $window 'DshWindow.RestartService'
    $previewLog = Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'window-preview.log'
    Check ((Get-Content -LiteralPath $previewLog -Raw).Contains('Offline preview restart callback requested; no production service was restarted.')) 'restart menu calls the isolated host callback'
    Check ((Get-Content -LiteralPath $previewLog -Raw).Contains('Offline preview restart preparation screen active=True.') -and
     $window.Current.NativeWindowHandle -eq $hostHandle.ToInt64()) 'restart callback brings back the preparation screen in the same native window'
    $restartBounds = $window.Current.BoundingRectangle
    $restartBitmap = [Drawing.Bitmap]::new([int]$restartBounds.Width,[int]$restartBounds.Height)
    $restartGraphics = [Drawing.Graphics]::FromImage($restartBitmap)
    try {
     $restartGraphics.CopyFromScreen([int]$restartBounds.X,[int]$restartBounds.Y,0,0,$restartBitmap.Size)
     $restartBitmap.Save((Join-Path $Output 'restart-native.png'),[Drawing.Imaging.ImageFormat]::Png)
    } finally { $restartGraphics.Dispose(); $restartBitmap.Dispose() }
    $deadline = [datetime]::UtcNow.AddSeconds(30)
    do {
     Start-Sleep -Milliseconds 250
     try { $restarted = Get-Content -LiteralPath $diagnostics -Raw | ConvertFrom-Json } catch { $restarted = $null }
    } while ((-not $restarted -or $restarted.sample -ne 2 -or [datetime]$restarted.capturedUtc -le $previousCapture) -and [datetime]::UtcNow -lt $deadline)
    Check ($restarted -and $restarted.sample -eq 2 -and [datetime]$restarted.capturedUtc -gt $previousCapture -and
     $restarted.page.elementCount -gt 50 -and -not $restarted.shell.startupVisible) 'restart preview reconnects to actual DSH and preparation screen fades out'
    $data = $restarted
    foreach ($entry in @(@{id='CheckLauncherInstaller';target='启动器/安装器'},@{id='CheckDsh';target='DSH'},@{id='CheckPlugins';target='插件'})) {
     $null = Invoke-ShellId $window 'DshWindow.CheckUpdates'
     $null = Invoke-ShellId $window ('DshWindow.' + $entry.id)
     $previewLog = Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'window-preview.log'
     Check ((Get-Content -LiteralPath $previewLog -Raw).Contains('Window update menu requested ' + $entry.target + '.')) "update menu invokes isolated $($entry.target) callback"
     $settingsNameCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'Dafeiyu-Go 设置')
     $settingsProcessCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$process.Id)
     $settingsCondition = [Windows.Automation.AndCondition]::new($settingsNameCondition,$settingsProcessCondition)
     Check ($null -eq [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$settingsCondition)) 'update action does not open settings'
    }
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
     $helpers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" | Where-Object Name -eq 'DafeiyuGo.Info.exe')
     if (-not $helpers.Count) { Start-Sleep -Milliseconds 200 }
    } while (-not $helpers.Count -and [datetime]::UtcNow -lt $deadline)
    Check ($helpers.Count -gt 0) 'update commands start the real isolated information-window helper'
    $null = Invoke-ShellId $window 'DshWindow.Settings'
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
     $settingsWindow = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$settingsCondition)
     if ($null -eq $settingsWindow) { Start-Sleep -Milliseconds 200 }
    } while ($null -eq $settingsWindow -and [datetime]::UtcNow -lt $deadline)
    Check ($null -ne $settingsWindow) 'settings command opens the actual WinUI settings window'
    $windowRadio = $settingsWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
      [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'PageOpenWindowRadio'))
    $browserRadio = $settingsWindow.FindFirst([Windows.Automation.TreeScope]::Descendants,
      [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'PageOpenBrowserRadio'))
    Check ($null -ne $windowRadio -and $null -ne $browserRadio) 'general page exposes application/browser radio options'
    Check ($windowRadio.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) 'isolated Window preference loads as application option'
    $browserRadio.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    $saved = Get-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Raw | ConvertFrom-Json
    Check ($saved.pageOpenMode -eq 'Browser') 'browser radio selection persists actual setting'
    $windowRadio.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    $saved = Get-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Raw | ConvertFrom-Json
    Check ($saved.pageOpenMode -eq 'Window') 'application radio selection persists actual setting'
    $settingsWindow.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    $presenceButton = Find-ShellId $window 'DshWindow.OnlinePresence'
    Check ($null -ne $presenceButton -and -not $presenceButton.Current.IsOffscreen) 'existing presence snapshot is displayed in the right title bar'
    $null = Invoke-ShellId $window 'DshWindow.OnlinePresence'
    $deadline = [datetime]::UtcNow.AddSeconds(10)
    do {
     $serverSettings = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$settingsCondition)
     if ($null -eq $serverSettings) { Start-Sleep -Milliseconds 100 }
    } while ($null -eq $serverSettings -and [datetime]::UtcNow -lt $deadline)
    Check ($null -ne $serverSettings) 'online presence opens the actual settings window'
    $serverPage = $serverSettings.FindFirst([Windows.Automation.TreeScope]::Descendants,
      [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'ServerMetricsStatusText'))
    Check ($null -ne $serverPage -and -not $serverPage.Current.IsOffscreen) 'online presence navigates directly to the server metrics page'
    $serverSettings.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    do {
     $helpers = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" | Where-Object Name -eq 'DafeiyuGo.Info.exe')
     if ($helpers.Count) { Start-Sleep -Milliseconds 200 }
    } while ($helpers.Count -and [datetime]::UtcNow -lt $deadline)
   }
   Confirm-Foreground $window $round
   $originalBounds = $window.Current.BoundingRectangle
   $compactWidth = [int](640 * $data.shell.dpiScale)
   $compactHeight = [int](420 * $data.shell.dpiScale)
   Check ([WindowSmokeNative]::MoveWindow($hostHandle,[int]$originalBounds.X,[int]$originalBounds.Y,$compactWidth,$compactHeight,$true)) "round ${round}: resize actual host to 640x420 DIP"
   Start-Sleep -Milliseconds 500
   $narrowBounds = $window.Current.BoundingRectangle
   $narrowButtons = @('ApplicationMenu','EditMenu','Settings','CheckUpdates') | ForEach-Object { Find-ShellId $window ('DshWindow.' + $_) }
   Check (@($narrowButtons | Where-Object { $_.Current.BoundingRectangle.Right -gt $narrowBounds.Right - 80 * $data.shell.dpiScale -or
     $_.Current.BoundingRectangle.Left -lt $narrowBounds.Left }).Count -eq 0) "round ${round}: narrow menu fits before system caption buttons"
   $compactBitmap = [Drawing.Bitmap]::new([int]$narrowBounds.Width,[int]$narrowBounds.Height)
   $compactGraphics = [Drawing.Graphics]::FromImage($compactBitmap)
   try {
    $compactGraphics.CopyFromScreen([int]$narrowBounds.X,[int]$narrowBounds.Y,0,0,$compactBitmap.Size)
    $compactBitmap.Save((Join-Path $Output "window-compact-$round.png"),[Drawing.Imaging.ImageFormat]::Png)
   } finally { $compactGraphics.Dispose(); $compactBitmap.Dispose() }
   [WindowSmokeNative]::MoveWindow($hostHandle,[int]$originalBounds.X,[int]$originalBounds.Y,[int]$originalBounds.Width,[int]$originalBounds.Height,$true) | Out-Null
   Start-Sleep -Milliseconds 500
   Confirm-Foreground $window $round
   $bounds = $window.Current.BoundingRectangle
   $bitmap = [Drawing.Bitmap]::new([int]$bounds.Width,[int]$bounds.Height)
   $graphics = [Drawing.Graphics]::FromImage($bitmap)
   try {
    $graphics.CopyFromScreen([int]$bounds.X,[int]$bounds.Y,0,0,$bitmap.Size)
    Check ([WindowSmokeNative]::GetForegroundWindow() -eq $hostHandle) "round ${round}: screenshot foreground remains the verified WinUI host"
    $colors = [Collections.Generic.HashSet[int]]::new()
    for ($x=20;$x -lt $bitmap.Width-20;$x+=19) { for($y=80;$y -lt $bitmap.Height-20;$y+=19) { [void]$colors.Add($bitmap.GetPixel($x,$y).ToArgb()) } }
    Check ($colors.Count -gt 12) "round ${round}: real window capture has rendered content"
    $bitmap.Save((Join-Path $Output "window-$round.png"),[Drawing.Imaging.ImageFormat]::Png)
   } finally { $graphics.Dispose();$bitmap.Dispose() }
   $samples = @()
   for ($sample=0;$sample -lt 3;$sample++) { $samples += Snapshot $process.Id; Start-Sleep -Milliseconds 1000 }
   $backend = if ($BackendProcessId) { Snapshot $BackendProcessId } else { $null }
   $webIds = @($samples[-1].processes | Where-Object name -eq 'msedgewebview2' | ForEach-Object id)
   Check ($webIds -contains [int]$data.browserProcessId) "round ${round}: diagnostics browser process belongs to this WinUI host tree"
   Check (@($samples[-1].processes | Where-Object { $_.name -match 'DafeiyuGo.Info|InfoHost|Installer|Sfx' }).Count -eq 0) "round ${round}: completed preview checks retain no installer or information helper process"
   $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
   Check ($process.WaitForExit(15000)) "round ${round}: normal native window close exits preview"
   $deadline = [datetime]::UtcNow.AddSeconds(15)
   do { $remaining = @($webIds | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }); if ($remaining.Count) { Start-Sleep -Milliseconds 250 } } while($remaining.Count -and [datetime]::UtcNow -lt $deadline)
   Check ($remaining.Count -eq 0) "round ${round}: owned WebView2 processes release after close"
   $rounds.Add(@{round=$round;profile=if($round -eq 1){'cold'}else{'warm'};page=$data;host=$hostEvidence;
     composer=$composer;windowSamples=$samples;backend=$backend;released=$remaining.Count -eq 0})
  } finally { if (-not $process.HasExited) { $process.Kill();$process.WaitForExit() } }
 }
} finally {
 @{checks=$checks;rounds=$rounds;webAccessibilityChecked=[bool]$VerifyWebAccessibility;scope='isolated actual DSH workspace; real menus/settings/information helper; update callbacks are explicit offline previews; no model command invoked; content UIA traversal is optional because this WebView runtime can block synchronous automation searches; no Electron comparison';hostOS=[Environment]::OSVersion.VersionString} |
  ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $Output 'result.json') -Encoding utf8
}
Write-Output "PASS $($checks.Count) window smoke checks; evidence: $Output"
