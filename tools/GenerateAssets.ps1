param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\LegacyEdge\Assets')
)

Add-Type -AssemblyName System.Drawing

$assetPath = [System.IO.Path]::GetFullPath($AssetDirectory)
[System.IO.Directory]::CreateDirectory($assetPath) | Out-Null

function New-LegacyEdgeAsset {
    param(
        [string]$Name,
        [int]$Width,
        [int]$Height,
        [switch]$IncludeName
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::FromArgb(0, 120, 215))

    $iconSize = [Math]::Min($Height * 0.70, $Width * $(if ($IncludeName) { 0.30 } else { 0.78 }))
    $iconFont = [System.Drawing.Font]::new('Segoe UI', [single]$iconSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $iconText = 'e'
    $iconMeasure = $graphics.MeasureString($iconText, $iconFont)
    $iconX = if ($IncludeName) { $Width * 0.12 } else { ($Width - $iconMeasure.Width) / 2 }
    $iconY = ($Height - $iconMeasure.Height) / 2 - ($Height * 0.04)
    $graphics.DrawString($iconText, $iconFont, $whiteBrush, [single]$iconX, [single]$iconY)

    if ($IncludeName) {
        $nameSize = [Math]::Max(12, $Height * 0.15)
        $nameFont = [System.Drawing.Font]::new('Segoe UI', [single]$nameSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $graphics.DrawString('Legacy Edge', $nameFont, $whiteBrush, [single]($Width * 0.38), [single](($Height - $nameSize * 1.7) / 2))
        $nameFont.Dispose()
    }

    $target = Join-Path $assetPath $Name
    $bitmap.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $whiteBrush.Dispose()
    $iconFont.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

New-LegacyEdgeAsset -Name 'Square44x44Logo.scale-200.png' -Width 88 -Height 88
New-LegacyEdgeAsset -Name 'Square44x44Logo.targetsize-24_altform-unplated.png' -Width 24 -Height 24
New-LegacyEdgeAsset -Name 'Square150x150Logo.scale-200.png' -Width 300 -Height 300
New-LegacyEdgeAsset -Name 'Wide310x150Logo.scale-200.png' -Width 620 -Height 300 -IncludeName
New-LegacyEdgeAsset -Name 'SplashScreen.scale-200.png' -Width 1240 -Height 600 -IncludeName
New-LegacyEdgeAsset -Name 'LockScreenLogo.scale-200.png' -Width 48 -Height 48
New-LegacyEdgeAsset -Name 'StoreLogo.png' -Width 50 -Height 50

Write-Output "Generated UWP visual assets in $assetPath"
