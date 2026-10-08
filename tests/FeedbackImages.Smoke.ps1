param([Parameter(Mandatory=$true)][string]$Dist, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FeedbackImageCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle,out RECT rect);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
[FeedbackImageCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY = Join-Path ([IO.Path]::GetFullPath($Output)) 'settings'
$env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY = Join-Path ([IO.Path]::GetFullPath($Output)) 'downloads'
New-Item -ItemType Directory -Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY -Force | Out-Null
@{legacyApiKeyMigrationCompleted=$true; dshRoot=(Join-Path $Output 'fixture-dsh'); apiKeyProtected=''; gitHubTokenProtected=''; adminTokenProtected=''} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY 'LauncherSettings.json') -Encoding utf8
$env:__COMPAT_LAYER = 'RunAsInvoker'
try { $process = Start-Process -FilePath (Join-Path $Dist 'DeepSeek Harness.Core.exe') -ArgumentList '--settings-preview=Feedback' -PassThru -WindowStyle Hidden }
finally { Remove-Item Env:__COMPAT_LAYER -ErrorAction SilentlyContinue }
$checks = 0
function Check([bool]$Condition, [string]$Label) { if (!$Condition) { throw $Label }; $script:checks++; Write-Output ('PASS ' + $Label) }
function Images {
    return @($script:window.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '展开反馈图片')) |
        Where-Object {!$_.Current.IsOffscreen} | Sort-Object {$_.Current.BoundingRectangle.Top}, {$_.Current.BoundingRectangle.Left})
}
function Capture([string]$Name) {
    $handle = [IntPtr]::new($script:window.Current.NativeWindowHandle)
    $script:rect = [FeedbackImageCapture+RECT]::new()
    [FeedbackImageCapture]::GetWindowRect($handle,[ref]$script:rect) | Out-Null
    $bitmap = [Drawing.Bitmap]::new($script:rect.Right-$script:rect.Left, $script:rect.Bottom-$script:rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { if (![FeedbackImageCapture]::PrintWindow($handle,$dc,2)) { throw 'Image screenshot capture failed.' } }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    $bitmap.Save((Join-Path $Output ($Name + '.png')),[Drawing.Imaging.ImageFormat]::Png)
    return $bitmap
}
function VerifyThumbnails([string]$Name) {
    $nodes = @(Images)
    Check ($nodes.Count -eq 6) "$Name contains three main and three supplement thumbnails"
    $bitmap = Capture $Name
    try {
        for ($index=0; $index -lt $nodes.Count; $index++) {
            $bounds = $nodes[$index].Current.BoundingRectangle
            Check ([Math]::Abs($bounds.Width-$bounds.Height) -lt 1 -and $bounds.Width -ge 90) "$Name thumbnail $index stays square"
            foreach ($point in @(@(0.25,0.25),@(0.75,0.25),@(0.25,0.75),@(0.75,0.75),@(0.5,0.5))) {
                $pixel = $bitmap.GetPixel([int]($bounds.Left-$script:rect.Left+$bounds.Width*$point[0]), [int]($bounds.Top-$script:rect.Top+$bounds.Height*$point[1]))
                $correct = if ($index % 3 -eq 2) {$pixel.R -gt 200 -and $pixel.G -lt 40 -and $pixel.B -gt 200} else {$pixel.R -lt 40 -and $pixel.G -gt 200 -and $pixel.B -lt 40}
                Check $correct "$Name thumbnail $index center crop at $($point -join ',')"
            }
        }
    } finally { $bitmap.Dispose() }
}
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw ('Image QA process exited: ' + $process.ExitCode) }
        $script:window = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)) |
            Where-Object {$_.Current.Name -eq 'Dafeiyu-Go 设置'} | Select-Object -First 1
        if ($script:window -and @(Images).Count -eq 6) { break }
        Start-Sleep -Milliseconds 250
    }
    Check ($null -ne $script:window) 'isolated image QA window opened'
    Start-Sleep -Seconds 2
    VerifyThumbnails 'feedback-images-centered'
    @(Images)[0].GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    $collapse = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '收起反馈图片'))
    Check ($null -ne $collapse -and !$collapse.Current.IsOffscreen) 'thumbnail expands into the full image'
    $bitmap = Capture 'feedback-image-expanded'
    try {
        $bounds = $collapse.Current.BoundingRectangle
        Check ([Math]::Abs($bounds.Width/$bounds.Height-3) -lt 0.1) 'expanded landscape image preserves original aspect ratio'
        foreach ($sample in @(@(0.17,'red'),@(0.5,'green'),@(0.83,'blue'))) {
            $pixel = $bitmap.GetPixel([int]($bounds.Left-$script:rect.Left+$bounds.Width*$sample[0]), [int]($bounds.Top-$script:rect.Top+$bounds.Height/2))
            $correct = switch ($sample[1]) {'red' {$pixel.R -gt 200 -and $pixel.G -lt 40}; 'green' {$pixel.G -gt 200 -and $pixel.R -lt 40}; 'blue' {$pixel.B -gt 200 -and $pixel.R -lt 40}}
            Check $correct ('expanded image retains ' + $sample[1] + ' stripe')
        }
    } finally { $bitmap.Dispose() }
    $collapse.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500
    VerifyThumbnails 'feedback-images-collapsed'
    Write-Output ("PASS $checks WinUI image checks; isolated HTTP and settings, no feedback submission.")
} finally {
    if ($process -and !$process.HasExited) { $process.CloseMainWindow() | Out-Null; if (!$process.WaitForExit(3000)) { Stop-Process -Id $process.Id } }
}
