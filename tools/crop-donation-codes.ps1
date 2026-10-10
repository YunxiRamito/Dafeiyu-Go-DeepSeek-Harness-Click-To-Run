param([string]$AssetDirectory = (Join-Path $PSScriptRoot '..\source\assets'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$crops = @(
    @{ Input = 'donate-alipay.jpg'; Output = 'donate-alipay-qr.png'; X = 295; Y = 505; Size = 670 },
    @{ Input = 'donate-wechat.png'; Output = 'donate-wechat-qr.png'; X = 327; Y = 367; Size = 602 }
)
foreach ($crop in $crops) {
    $source = [System.Drawing.Image]::FromFile((Join-Path $AssetDirectory $crop.Input))
    $target = New-Object System.Drawing.Bitmap $crop.Size,$crop.Size
    $graphics = [System.Drawing.Graphics]::FromImage($target)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.DrawImage($source,
            [System.Drawing.Rectangle]::new(0, 0, $crop.Size, $crop.Size),
            [System.Drawing.Rectangle]::new($crop.X, $crop.Y, $crop.Size, $crop.Size),
            [System.Drawing.GraphicsUnit]::Pixel)
        $target.Save((Join-Path $AssetDirectory $crop.Output), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $target.Dispose(); $source.Dispose() }
}
