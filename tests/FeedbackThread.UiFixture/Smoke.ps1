param([Parameter(Mandatory=$true)][string]$Dist, [Parameter(Mandatory=$true)][string]$Output,
    [switch]$StatusPreviewOnly, [switch]$VotePreviewOnly, [ValidateSet('Light','Dark')][string]$Theme = 'Light',
    [ValidatePattern('^#[0-9A-Fa-f]{6}$')][string]$AccentHex = '#067ACB')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class FeedbackThreadCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle,out RECT rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle,IntPtr after,int left,int top,int width,int height,uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int left,int top);
 [DllImport("user32.dll")] public static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
}
"@
[FeedbackThreadCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
$outputRoot = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a fresh smoke output directory.' }
$env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY = Join-Path $outputRoot 'settings'
$env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY = Join-Path $outputRoot 'downloads'
New-Item -ItemType Directory -Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY | Out-Null
@{legacyApiKeyMigrationCompleted=$true; dshRoot=(Join-Path $outputRoot 'fixture-dsh'); theme=$Theme; material='None'; accentSource='Custom'; accentColor=$AccentHex} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Encoding utf8
$env:__COMPAT_LAYER = 'RunAsInvoker'
if ($StatusPreviewOnly) { $env:DAFEIYU_FEEDBACK_UI_QA = 'Statuses' }
if ($VotePreviewOnly) { $env:DAFEIYU_FEEDBACK_UI_QA = 'Votes' }
try { $process = Start-Process -FilePath (Join-Path $Dist 'DeepSeek Harness.Core.exe') -ArgumentList '--settings-preview=Feedback' -PassThru -WindowStyle Hidden }
finally { Remove-Item Env:__COMPAT_LAYER -ErrorAction SilentlyContinue }
$checks = 0
function Check([bool]$Condition,[string]$Label) { if (!$Condition) { throw $Label }; $script:checks++; Write-Output ('PASS ' + $Label) }
function Nodes {
    @($script:window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition))
}
function Named([string]$Name) {
    @(Nodes | Where-Object { $_.Current.Name -eq $Name }) | Select-Object -First 1
}
function WaitNamed([string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        $node = Named $Name
        if ($node -and ($node.Current.ControlType -ne [Windows.Automation.ControlType]::Button -or $node.Current.IsEnabled)) { return $node }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    @(Nodes | ForEach-Object { @{name=$_.Current.Name; type=$_.Current.ControlType.ProgrammaticName; offscreen=$_.Current.IsOffscreen} }) |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $outputRoot 'missing-control-tree.json') -Encoding utf8
    Snapshot 'missing-control' 1200 | Out-Host
    return $null
}
function InvokeNode($Node) {
    Check ($null -ne $Node) 'command exists'
    if (!$Node.Current.IsEnabled) { $Node = WaitNamed $Node.Current.Name }
    $Node.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
}
function ClickNode($Node) {
    Check ($null -ne $Node -and $Node.Current.IsEnabled -and !$Node.Current.IsOffscreen) 'visible command exists'
    [FeedbackThreadCapture]::SetForegroundWindow([IntPtr]::new($script:window.Current.NativeWindowHandle)) | Out-Null
    $bounds = $Node.Current.BoundingRectangle
    [FeedbackThreadCapture]::SetCursorPos([int]($bounds.Left+$bounds.Width/2),[int]($bounds.Top+$bounds.Height/2)) | Out-Null
    Start-Sleep -Milliseconds 150
    [FeedbackThreadCapture]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
    [FeedbackThreadCapture]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500
}
function Snapshot([string]$Name,[int]$Width) {
    $handle = [IntPtr]::new($script:window.Current.NativeWindowHandle)
    [FeedbackThreadCapture]::SetWindowPos($handle,[IntPtr]::Zero,20,20,$Width,960,4) | Out-Null
    Start-Sleep -Milliseconds 650
    $rect = [FeedbackThreadCapture+RECT]::new()
    [FeedbackThreadCapture]::GetWindowRect($handle,[ref]$rect) | Out-Null
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { Check ([FeedbackThreadCapture]::PrintWindow($handle,$dc,2)) 'screenshot rendered' }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $outputRoot ($Name + '.png')),[Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw ('Fixture process exited: ' + $process.ExitCode) }
        $script:window = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)) |
            Where-Object {$_.Current.Name -eq 'Dafeiyu-Go 设置'} | Select-Object -First 1
        if ($script:window -and @(Nodes | Where-Object {$_.Current.Name -like '*#42*'}).Count -gt 0) { break }
        Start-Sleep -Milliseconds 250
    }
    Check ($null -ne $script:window) 'feedback preview opened'
    Check (@(Nodes | Where-Object {$_.Current.Name -like '*#42*'}).Count -gt 0) 'stable feedback number appears'
    Snapshot 'feedback-thread-desktop' 1200
    Snapshot 'feedback-thread-narrow' 850
    if ($VotePreviewOnly) {
        Check ($null -ne (Named '每页 10 条 · 共 25 条')) 'server paging shows ten per page'
        $vote = WaitNamed '支持反馈 #42，7 人'
        $voteBounds = $vote.Current.BoundingRectangle
        $scale = [FeedbackThreadCapture]::GetDpiForWindow([IntPtr]::new($script:window.Current.NativeWindowHandle)) / 96.0
        Check ($voteBounds.Width -le 58 * $scale -and $voteBounds.Height -le 34 * $scale) 'support control remains compact'
        $heading = Named '漏洞反馈 #42'
        Check (($voteBounds.Left -gt $heading.Current.BoundingRectangle.Left + $heading.Current.BoundingRectangle.Width * 0.7) -and ($voteBounds.Top -gt $heading.Current.BoundingRectangle.Bottom)) 'support control sits at the original feedback lower right'
        InvokeNode $vote
        $selected = WaitNamed '取消支持反馈 #42，8 人'
        Check ($null -ne $selected) 'vote changes to filled support icon with updated count'
        Snapshot 'support-selected' 1200
        $bitmap = [Drawing.Bitmap]::new((Join-Path $outputRoot 'support-selected.png'))
        try {
            $rect = [FeedbackThreadCapture+RECT]::new()
            [FeedbackThreadCapture]::GetWindowRect([IntPtr]::new($script:window.Current.NativeWindowHandle),[ref]$rect) | Out-Null
            $bounds = $selected.Current.BoundingRectangle
            $red = [Convert]::ToInt32($AccentHex.Substring(1,2),16)
            $green = [Convert]::ToInt32($AccentHex.Substring(3,2),16)
            $blue = [Convert]::ToInt32($AccentHex.Substring(5,2),16)
            $matching = 0
            for ($row = [int]($bounds.Top-$rect.Top); $row -lt [int]($bounds.Bottom-$rect.Top); $row++) {
                for ($column = [int]($bounds.Left-$rect.Left); $column -lt [int]($bounds.Right-$rect.Left); $column++) {
                    $pixel = $bitmap.GetPixel($column,$row)
                    if ([Math]::Abs($pixel.R-$red) -lt 8 -and [Math]::Abs($pixel.G-$green) -lt 8 -and [Math]::Abs($pixel.B-$blue) -lt 8) { $matching++ }
                }
            }
            Check ($matching -gt 8) 'selected support icon and count render with the configured accent color'
        }
        finally { $bitmap.Dispose() }
        InvokeNode $selected
        Check ($null -ne (WaitNamed '支持反馈 #42，7 人')) 'cancel restores outlined support icon and count'
        Snapshot 'support-cancelled' 1200
        $next = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'FeedbackNextPageButton'))
        InvokeNode $next
        Check ($null -ne (WaitNamed '漏洞反馈 #52')) 'second server page loads next ten records'
        InvokeNode $next
        Check ($null -ne (WaitNamed '漏洞反馈 #62')) 'last server page loads remaining five records'
        $requests = Get-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'fixture-pages.log')
        Check (($requests -match 'offset=10 limit=10').Count -gt 0 -and ($requests -match 'offset=20 limit=10').Count -gt 0) 'page navigation requests server offsets 10 and 20'
        $votes = Get-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'fixture-votes.log')
        Check ($votes.Count -eq 2 -and $votes[0] -match 'POST' -and $votes[1] -match 'DELETE') 'vote and cancel each send one write request'
        Write-Output ("PASS $checks isolated $Theme medal and paging checks")
        return
    }
    if ($StatusPreviewOnly) {
        foreach ($label in @('已提交','处理中','已完成','暂不处理')) {
            Check (@(Nodes | Where-Object {$_.Current.Name -eq $label -and $_.Current.ControlType -eq [Windows.Automation.ControlType]::Text}).Count -gt 0) ('status badge exists: ' + $label)
        }
        Write-Output ("PASS $checks isolated $Theme status badge checks")
        return
    }
    $body = @(Nodes | Where-Object {$_.Current.Name -like 'Root feedback:*'}) | Select-Object -First 1
    Check ($null -ne $body -and $body.Current.Name.Length -lt 400) 'long feedback starts collapsed'
    $textPattern = $null
    Check ($body.TryGetCurrentPattern([Windows.Automation.TextPattern]::Pattern,[ref]$textPattern)) 'body exposes selectable text'
    $expand = @(Nodes | Where-Object {$_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -like '*展开*' -and $_.Current.Name -notlike '*图片*'}) | Select-Object -First 1
    InvokeNode $expand
    Check (@(Nodes | Where-Object {$_.Current.Name -like 'Root feedback:*' -and $_.Current.Name.Length -gt 1000}).Count -gt 0) 'expand reveals complete body'
    $collapse = @(Nodes | Where-Object {$_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and $_.Current.Name -like '*收起*' -and $_.Current.Name -notlike '*图片*'}) | Select-Object -First 1
    InvokeNode $collapse
    ClickNode (Named '提交反馈')
    $logs = WaitNamed '随反馈上传启动器日志'
    if (!$logs) {
        ClickNode (Named '提交反馈')
        $logs = WaitNamed '随反馈上传启动器日志'
    }
    Check ($null -ne $logs) 'new feedback log checkbox appears'
    Check ($logs.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On) 'new feedback includes logs by default'
    InvokeNode (Named '取消')
    InvokeNode (Named '添加补充 · #42')
    $logs = WaitNamed '随补充上传启动器日志'
    Check ($null -ne $logs) 'supplement log checkbox appears'
    Check ($logs.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::Off) 'supplement logs default unchecked'
    Check ($null -ne (Named '添加图片')) 'supplements retain image attachments'
    InvokeNode (Named '取消')
    InvokeNode (Named '对话下一页 · #42')
    Check (@(Nodes | Where-Object {$_.Current.Name -like 'Developer message 8:*'}).Count -gt 0) 'older conversation page loads'
    InvokeNode (Named '对话上一页 · #42')
    Check (@(Nodes | Where-Object {$_.Current.Name -like 'Developer message 60:*'}).Count -gt 0) 'newer conversation page restores'
    Check (!(Test-Path -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'fixture-requests.log'))) 'fixture made no write requests'
    Write-Output ("PASS $checks isolated WinUI feedback checks")
}
finally {
    if ($process -and !$process.HasExited) { $process.CloseMainWindow() | Out-Null; if (!$process.WaitForExit(3000)) { Stop-Process -Id $process.Id } }
    Remove-Item Env:DAFEIYU_FEEDBACK_UI_QA -ErrorAction SilentlyContinue
}
