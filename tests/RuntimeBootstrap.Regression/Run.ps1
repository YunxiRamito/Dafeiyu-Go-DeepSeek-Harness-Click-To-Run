<#
Windows CI: pwsh -NoProfile -File ./tests/RuntimeBootstrap.Regression/Run.ps1
Optional: -BootstrapPath <built outer executable> tests an existing release artifact.
Optional: -BootstrapSource <RuntimeBootstrap.cs> compiles another source tree read-only.
Uses the inbox .NET Framework compiler and executes the production detector without
starting the launcher UI, DSH, installers, or modifying registry/user settings.
#>
[CmdletBinding()]
param([string]$BootstrapPath, [string]$BootstrapSource)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if ($BootstrapPath -and $BootstrapSource) { throw 'Use BootstrapPath or BootstrapSource, not both.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw "The x64 .NET Framework compiler is required: $compiler"
}
$fixture = Join-Path $root ('.build-temp\runtime-bootstrap-' + [guid]::NewGuid().ToString('N'))
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    $env:TEMP = $fixture
    $env:TMP = $fixture
    if ($BootstrapPath) {
        $BootstrapPath = (Resolve-Path -LiteralPath $BootstrapPath).Path
    } else {
        if (-not $BootstrapSource) { $BootstrapSource = Join-Path $root 'source\RuntimeBootstrap.cs' }
        $BootstrapSource = (Resolve-Path -LiteralPath $BootstrapSource).Path
        $sourceRoot = Split-Path $BootstrapSource -Parent
        $BootstrapPath = Join-Path $fixture 'DeepSeek Harness.exe'
        & $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output `
            "/win32icon:$(Join-Path $sourceRoot 'DeepSeekHarness.ico')" `
            "/win32manifest:$(Join-Path $sourceRoot 'RuntimeBootstrap.manifest')" `
            "/out:$BootstrapPath" $BootstrapSource (Join-Path $sourceRoot 'WindowsAppRuntimeDetector.cs')
        if ($LASTEXITCODE -ne 0) { throw "Production bootstrap compilation failed: $LASTEXITCODE" }
    }
    $runner = Join-Path $fixture 'RuntimeBootstrap.Regression.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /utf8output `
        "/out:$runner" (Join-Path $PSScriptRoot 'Program.cs')
    if ($LASTEXITCODE -ne 0) { throw "Regression runner compilation failed: $LASTEXITCODE" }
    & $runner $BootstrapPath
    if ($LASTEXITCODE -ne 0) { throw "Native bootstrap regressions failed: $LASTEXITCODE" }

    # Load only the verification function; the rest of verify.ps1 requires a full release dist.
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $root 'verify.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw "verify.ps1 syntax errors: $parseErrors" }
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Test-WindowsAppRuntime'
    }, $true)
    if ($null -eq $function) { throw 'verify.ps1 production detector function is missing.' }
    . ([scriptblock]::Create($function.Extent.Text))
    $actual = Test-WindowsAppRuntime $BootstrapPath
    if ($actual -isnot [bool]) { throw 'verify.ps1 detector did not return a Boolean.' }
    Write-Host "PASS verify.ps1 compiled bootstrap detector smoke: ready=$actual"
} finally {
    $env:TEMP = $previousTemp
    $env:TMP = $previousTmp
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $root '.build-temp')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a fixture outside .build-temp: $resolvedFixture"
    }
    if (Test-Path -LiteralPath $resolvedFixture) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
