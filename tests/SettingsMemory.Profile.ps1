param(
    [Parameter(Mandatory=$true)][string]$BaselineDist,
    [Parameter(Mandatory=$true)][string]$AfterDist,
    [Parameter(Mandatory=$true)][string]$Output,
    [int]$Runs = 2
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$Output = [IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$samples = [Collections.Generic.List[object]]::new()
$previousSettings = $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY
$previousHistory = $env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY
$previousCompat = $env:__COMPAT_LAYER

function Find-Control([string]$Id) {
    $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Select-Control([string]$Id) {
    $node = Find-Control $Id
    if (!$node) { throw "Missing control: $Id" }
    $node.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}
function Measure-Stage([string]$Stage, [string]$Version, [int]$Run) {
    for ($index=0; $index -lt 5; $index++) {
        $script:process.Refresh()
        if ($script:process.HasExited) { throw 'Memory preview exited during measurement.' }
        $samples.Add([pscustomobject]@{version=$Version;run=$Run;stage=$Stage;sample=$index;
            privateBytes=$script:process.PrivateMemorySize64;workingSet=$script:process.WorkingSet64;
            peakWorkingSet=$script:process.PeakWorkingSet64})
        Start-Sleep -Milliseconds 400
    }
}
function Seed-Fixture([string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $root = Join-Path $Directory 'fixture-dsh'
    $profile = Join-Path $root '.dsh\profiles\web'
    New-Item -ItemType Directory -Path $profile -Force | Out-Null
    $dependencies = @{}
    $bundles = @()
    for ($index=0; $index -lt 24; $index++) {
        $name = 'memory-local-' + $index.ToString('D2')
        $dependencies[$name] = '1.0.0'
        $bundles += $name
    }
    @{dependencies=$dependencies;dsh=@{profile=@{bundles=$bundles}}} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $profile 'package.json') -Encoding utf8
    @{legacyApiKeyMigrationCompleted=$true;dshRoot=$root;apiKeyProtected='';gitHubTokenProtected='';adminTokenProtected='';
        updateSource='Official';launcherUpdateMode='Off';installerUpdateMode='Off';dshUpdateMode='Off';pluginUpdateMode='Off';patchUpdateMode='Off'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'LauncherSettings.json') -Encoding utf8
    $items = @(for ($index=0; $index -lt 120; $index++) {
        @{owner='memory-fixture';repository=('plugin-' + $index.ToString('D3'));description='Memory profile fixture';category='其他工具';
            version='1.0.0';fromMarket=$true;stars=$index;imageUrl='http://fixture.invalid/icon.png'}
    })
    @{fetchedAtUtc=[DateTime]::UtcNow.ToString('o');fromMarket=$true;items=$items} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $Directory 'PluginCatalogCache.json') -Encoding utf8
    @{schemaVersion=1;items=@($items | Select-Object -First 6)} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $Directory 'FeaturedPlugins.json') -Encoding utf8
}

try {
    for ($run=1; $run -le $Runs; $run++) {
        foreach ($variant in @(@{name='baseline';dist=$BaselineDist}, @{name='after';dist=$AfterDist})) {
            $fixture = Join-Path $Output ($variant.name + '-' + $run)
            $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY = Join-Path $fixture 'settings'
            $env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY = Join-Path $fixture 'downloads'
            Seed-Fixture $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY
            $env:__COMPAT_LAYER = 'RunAsInvoker'
            $script:process = Start-Process -FilePath (Join-Path $variant.dist 'DeepSeek Harness.Core.exe') -ArgumentList '--settings-preview=General' -PassThru -WindowStyle Hidden
            try {
                $deadline = [DateTime]::UtcNow.AddSeconds(35)
                $script:window = $null
                while ([DateTime]::UtcNow -lt $deadline) {
                    $script:process.Refresh()
                    if ($script:process.HasExited) { throw ('Memory preview exited: ' + $script:process.ExitCode) }
                    $script:window = [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
                        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:process.Id)) |
                        Where-Object {$_.Current.Name -eq 'Dafeiyu-Go 设置'} | Select-Object -First 1
                    if ($script:window) { break }
                    Start-Sleep -Milliseconds 250
                }
                if (!$script:window) { throw 'Memory preview window did not appear.' }
                Start-Sleep -Seconds 6
                Measure-Stage 'general-before-plugins' $variant.name $run
                Select-Control 'FeaturesNavItem'
                Select-Control 'OnlinePluginsTab'
                Start-Sleep -Seconds 2
                if (!(Find-Control 'OnlinePluginsTab').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
                    throw 'First plugin visit failed.'
                }
                Measure-Stage 'plugins-online' $variant.name $run
                Select-Control 'LocalPluginsTab'
                $summary = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants,
                    [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, '共 24 个已安装插件'))
                if (!$summary) {
                    $script:window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition) |
                        ForEach-Object {$_.Current.Name + ' | ' + $_.Current.AutomationId} |
                        Set-Content -LiteralPath (Join-Path $fixture 'uia-names.txt') -Encoding utf8
                    throw 'Local plugins did not load on first visit.'
                }
                Select-Control 'HomeNavItem'
                Select-Control 'FeaturesNavItem'
                if (!(Find-Control 'LocalPluginsTab').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
                    throw 'Plugin tab was reset after returning to the page.'
                }
                Select-Control 'HomeNavItem'
                Start-Sleep -Seconds 2
                Measure-Stage 'home-after-plugins' $variant.name $run
                Write-Output ('PASS ' + $variant.name + ' run ' + $run + ': first plugin load and retained tabs')
            }
            finally {
                if ($script:process -and !$script:process.HasExited) {
                    $script:process.CloseMainWindow() | Out-Null
                    if (!$script:process.WaitForExit(3000)) { Stop-Process -Id $script:process.Id }
                }
            }
        }
    }
    $samples | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Output 'memory-samples.json') -Encoding utf8
    $summary = @($samples | Group-Object version,stage | ForEach-Object {
        $first = $_.Group[0]
        [pscustomobject]@{version=$first.version;stage=$first.stage;
            privateMiB=[Math]::Round(($_.Group | Measure-Object privateBytes -Average).Average/1MB,2);
            workingSetMiB=[Math]::Round(($_.Group | Measure-Object workingSet -Average).Average/1MB,2)}
    })
    $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Output 'memory-summary.json') -Encoding utf8
    $summary | Format-Table -AutoSize
}
finally {
    $env:DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY = $previousSettings
    $env:DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY = $previousHistory
    $env:__COMPAT_LAYER = $previousCompat
}
