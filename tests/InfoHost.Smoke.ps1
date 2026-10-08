param([string]$Dist, [string]$Output)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InfoCapture {
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 private delegate bool EnumProc(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
 [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
 public static IntPtr Find(int pid) {
  IntPtr found=IntPtr.Zero;
  EnumWindows((h,p) => { uint id; GetWindowThreadProcessId(h,out id); if(id==pid && IsWindowVisible(h)) {found=h;return false;}return true; },IntPtr.Zero);
  return found;
 }
}
'@
[InfoCapture]::SetThreadDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null
New-Item -ItemType Directory -Force $Output | Out-Null
$name = 'DafeiyuGo.Info.' + [guid]::NewGuid().ToString('N')
$pipe = [IO.Pipes.NamedPipeServerStream]::new($name,[IO.Pipes.PipeDirection]::Out,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
if (Test-Path (Join-Path $Dist 'info-host')) { $Dist = Join-Path $Dist 'info-host' }
$process = Start-Process -FilePath (Join-Path $Dist 'DafeiyuGo.Info.exe') -ArgumentList "$name $PID" -WorkingDirectory $Dist -WindowStyle Hidden -PassThru
$writer = $null
try {
  $timeout = [Threading.CancellationTokenSource]::new(8000)
  $pipe.WaitForConnectionAsync($timeout.Token).GetAwaiter().GetResult()
  $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false)); $writer.AutoFlush=$true
  $states = @(
   @{Command='Working';Title='正在检查更新';Detail='正在读取版本清单';Percent=-1;Outcome='';Dark=$false},
   @{Command='Update';Title='正在更新 DSH';Detail='下载中 12.0MB/24.0MB';Percent=50;Outcome='';Dark=$false},
   @{Command='Working';Title='正在启动 DSH 服务';Detail='等待本地服务端口就绪';Percent=-1;Outcome='';Dark=$false},
   @{Command='Notice';NoticeId='smoke-short';Title='开发者通知';Detail='**粗体**，*斜体*，<u>下划线</u>，~~删除线~~。'+"`n"+'<span style="font-size:24px">字号 24</span>'+"`n"+'- 列表一'+"`n"+'- 列表二';PublishedAt='2026-10-08';Buttons=@('查看设置','稍后再说');Theme='Dark'},
   @{Command='Notice';NoticeId='smoke-long';Title='长正文通知';Detail=('正文内容 **粗体** <u>下划线</u> '+"`n")*50;PublishedAt='2026-10-08';Buttons=@('打开设置','查看详情');Theme='Dark'},
   @{Command='Complete';Title='DSH 服务已启动';Detail='服务已就绪';Percent=-1;Outcome='Success';Dark=$false;PlayChime=$false},
   @{Command='Complete';Title='更新失败';Detail='检查连接后重试';Percent=-1;Outcome='Failure';Dark=$false},
   @{Command='Complete';Title='需要检查运行环境';Detail='请确认网络状态';Percent=-1;Outcome='Warning';Dark=$false},
   @{Command='Working';Title='正在检查更新';Detail='正在读取版本清单';Percent=-1;Outcome='';Dark=$false},
   @{Command='Complete';Title='大肥鱼后端服务器离线';Detail='您可能无法及时收到公告与通知，不影响依靠后端的基础功能，以及DeepSeek Harness的使用。';Percent=-1;Outcome='Warning';Dark=$false;PlayChime=$true},
   @{Command='Complete';Title='你的大肥鱼又上线了';Detail='后端服务器已恢复在线状态，所有功能均可正常使用。';Percent=-1;Outcome='Success';Dark=$false;PlayChime=$true},
   @{Command='Complete';Title='DSH 服务已重启';Detail='服务已就绪';Percent=-1;Outcome='Success';Dark=$false;PlayChime=$false},
   @{Command='Complete';Title='反馈有新回复';Detail='收到';Percent=-1;Outcome='Information';Theme='Dark';PlayChime=$true}
  )
  $bounds = @()
  for ($i=0; $i -lt $states.Count; $i++) {
   $writer.WriteLine(($states[$i] | ConvertTo-Json -Compress)); Start-Sleep -Milliseconds 550
   $process.Refresh(); if ($process.HasExited) { throw 'Information helper exited before completion' }
   $r = [InfoCapture+RECT]::new(); $handle = [InfoCapture]::Find($process.Id)
   if ($handle -eq [IntPtr]::Zero) { throw 'Information helper window missing' }
   [InfoCapture]::GetWindowRect($handle,[ref]$r) | Out-Null
   if ($states[$i].Command -eq 'Notice') {
    $element = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $nodes = $element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    foreach ($expected in @($states[$i].Title, '2026-10-08', '已读') + $states[$i].Buttons) {
     $node = $nodes | Where-Object { $_.Current.Name -eq $expected -and !$_.Current.IsOffscreen } | Select-Object -First 1
     if (!$node) { throw "Visible notice control missing: $expected" }
     $boundsNode = $node.Current.BoundingRectangle
     if ($boundsNode.Top -lt $r.Top -or $boundsNode.Bottom -gt $r.Bottom -or $boundsNode.Left -lt $r.Left -or $boundsNode.Right -gt $r.Right) { throw "Notice control clipped: $expected" }
    }
    if ($r.Bottom-$r.Top -gt 480) { throw 'Notice exceeds maximum window height' }
    if ($r.Right-$r.Left -ne 480) { throw 'Notice must use wider window' }
    foreach ($label in $states[$i].Buttons) {
     $node = $nodes | Where-Object { $_.Current.Name -eq $label -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | Select-Object -First 1
     if ($node.Current.BoundingRectangle.Height -gt 35) { throw "Button label wrapped: $label" }
    }
   }
   if ($states[$i].Command -eq 'Complete') {
    $element = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $nodes = $element.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)
    $label = switch ($states[$i].Outcome) { 'Success' {'成功'} 'Failure' {'失败'} 'Information' {'信息提醒'} default {'警告'} }
    $icon = $nodes | Where-Object { $_.Current.Name -eq $label -and !$_.Current.IsOffscreen } | Select-Object -First 1
    if (!$icon) { throw "Result icon missing after state switch: $label" }
    $titleNode = $nodes | Where-Object { $_.Current.Name -eq $states[$i].Title } | Select-Object -First 1
    $detailNode = $nodes | Where-Object { $_.Current.Name -eq $states[$i].Detail -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | Select-Object -First 1
    $topGap = $titleNode.Current.BoundingRectangle.Top - $r.Top
    $bottomGap = $r.Bottom - $detailNode.Current.BoundingRectangle.Bottom
    if ([Math]::Abs($topGap-$bottomGap) -gt 4) { throw "Result text margins asymmetric: top=$topGap bottom=$bottomGap" }
   }
   $bounds += [pscustomobject]@{Stage=$i;Left=$r.Left;Top=$r.Top;Right=$r.Right;Bottom=$r.Bottom;Width=($r.Right-$r.Left);Height=($r.Bottom-$r.Top)}
   $bitmap = [Drawing.Bitmap]::new(($r.Right-$r.Left),($r.Bottom-$r.Top)); $g = [Drawing.Graphics]::FromImage($bitmap)
   $dc = $g.GetHdc()
   try { if (![InfoCapture]::PrintWindow($handle,$dc,2)) { throw 'Information helper capture failed' } }
   finally { $g.ReleaseHdc($dc) }
   $bitmap.Save((Join-Path $Output "info-$i.png"),[Drawing.Imaging.ImageFormat]::Png)
   $g.Dispose(); $bitmap.Dispose()
   if ($i -eq 1) {
    try { $writer.Dispose() } catch {}; try { $pipe.Dispose() } catch {}; $writer = $null
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($name,[IO.Pipes.PipeDirection]::Out,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
    $timeout = [Threading.CancellationTokenSource]::new(8000); $pipe.WaitForConnectionAsync($timeout.Token).GetAwaiter().GetResult()
    $writer = [IO.StreamWriter]::new($pipe,[Text.UTF8Encoding]::new($false)); $writer.AutoFlush=$true
   }
  }
  if ($bounds[1].Height -le $bounds[2].Height) { throw 'Download to startup must shrink the window' }
  if (($bounds.Right | Select-Object -Unique).Count -ne 1 -or ($bounds.Bottom | Select-Object -Unique).Count -ne 1) { throw 'Bottom-right anchor moved during resize' }
  if ($process.WaitForExit(3500)) { throw 'Feedback reply closed before its five-second display lifetime' }
  if (!$process.WaitForExit(2500)) { throw 'Feedback reply did not auto close after five seconds' }
  $bounds | ConvertTo-Json | Set-Content (Join-Path $Output 'info-bounds.json') -Encoding utf8; $bounds | Format-Table -AutoSize
  Write-Host 'PASS independent information helper: work/download/notice/result states, visible title/date/buttons, anchored resize, automatic close'
} finally {
  if ($writer) { try { $writer.Dispose() } catch {} }
  if ($pipe) { try { $pipe.Dispose() } catch {} }
  if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
