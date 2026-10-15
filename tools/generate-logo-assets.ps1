$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $root 'Assets'
$source = [System.Drawing.Bitmap]::new((Join-Path $assets 'haruphoto-logo-master.png'))

# Preserve the approved artwork; only normalize its whitespace for small icons.
$left = $source.Width; $top = $source.Height; $right = 0; $bottom = 0
for ($y = 0; $y -lt $source.Height; $y += 2) {
    for ($x = 0; $x -lt $source.Width; $x += 2) {
        $pixel = $source.GetPixel($x, $y)
        if ($pixel.R -lt 80 -and $pixel.G -lt 80 -and $pixel.B -lt 80) {
            $left = [Math]::Min($left, $x); $right = [Math]::Max($right, $x)
            $top = [Math]::Min($top, $y); $bottom = [Math]::Max($bottom, $y)
        }
    }
}
if ($right -le $left) { throw 'The master image does not contain the black logo.' }
$side = [Math]::Min([Math]::Min($source.Width, $source.Height), [Math]::Ceiling([Math]::Max($right - $left, $bottom - $top) * 1.32))
$crop = [System.Drawing.RectangleF]::new(($left + $right - $side) / 2, ($top + $bottom - $side) / 2, $side, $side)

function New-LogoBitmap([int]$width, [int]$height) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::White)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $length = [Math]::Min($width, $height)
        $destination = [System.Drawing.RectangleF]::new(($width - $length) / 2, ($height - $length) / 2, $length, $length)
        $graphics.DrawImage($source, $destination, $crop, [System.Drawing.GraphicsUnit]::Pixel)
    } finally { $graphics.Dispose() }
    return $bitmap
}

function Save-Logo([string]$name, [int]$width, [int]$height) {
    $bitmap = New-LogoBitmap $width $height
    try { $bitmap.Save((Join-Path $assets $name), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
}

try {
    foreach ($size in 16, 24, 32, 48, 64, 128, 256, 1024) { Save-Logo "icon_$size.png" $size $size }
    Save-Logo 'AppIcon.png' 256 256
    Save-Logo 'Square44x44Logo.scale-200.png' 88 88
    Save-Logo 'Square44x44Logo.targetsize-24_altform-unplated.png' 24 24
    Save-Logo 'Square44x44Logo.targetsize-48_altform-lightunplated.png' 48 48
    Save-Logo 'Square150x150Logo.scale-200.png' 300 300
    Save-Logo 'Wide310x150Logo.scale-200.png' 620 300
    Save-Logo 'StoreLogo.png' 50 50
    Save-Logo 'SplashScreen.scale-200.png' 1240 600
    Save-Logo 'LockScreenLogo.scale-200.png' 48 48
    Copy-Item (Join-Path $assets 'icon_1024.png') (Join-Path $root 'docs\logo.png') -Force

    $sizes = @(16, 24, 32, 48, 64, 128, 256)
    $writer = [System.IO.BinaryWriter]::new([System.IO.File]::Create((Join-Path $assets 'AppIcon.ico')))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        foreach ($size in $sizes) {
            $bytes = [System.IO.File]::ReadAllBytes((Join-Path $assets "icon_$size.png"))
            $dimension = if ($size -eq 256) { [byte]0 } else { [byte]$size }
            $writer.Write($dimension); $writer.Write($dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]$offset)
            $offset += $bytes.Length
        }
        foreach ($size in $sizes) { $writer.Write([System.IO.File]::ReadAllBytes((Join-Path $assets "icon_$size.png"))) }
    } finally { $writer.Dispose() }
} finally { $source.Dispose() }
