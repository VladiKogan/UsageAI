param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ReviewSheet
)

# Renders the "tank" logo: a ring holding the quota that is left. Writes Resources\app.ico,
# Resources\logo.png, Resources\logo.svg and extension\media\logo.png from one set of geometry,
# using only System.Drawing, so the icon and both logos can never drift apart.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$tileColor = [System.Drawing.Color]::FromArgb(31, 31, 31)
$ringColor = [System.Drawing.Color]::FromArgb(241, 241, 239)
$waterColor = [System.Drawing.Color]::FromArgb(96, 205, 255)

# Geometry is in a 48-unit tile. At 24 px and below a wave turns to noise, so small sizes get a
# thicker ring and a flat waterline.
function Get-LogoSpec([int]$size) {
    if ($size -le 24) {
        return @{ RingRadius = 15.0; RingWidth = 4.6; WaterRadius = 10.2; Drop = 1.5; Amplitude = 0.0; Waves = 4 }
    }

    return @{ RingRadius = 14.5; RingWidth = 3.4; WaterRadius = 10.6; Drop = 1.6; Amplitude = 1.0; Waves = 4 }
}

function Get-WaterSegments($spec) {
    $half = [Math]::Sqrt($spec.WaterRadius * $spec.WaterRadius - $spec.Drop * $spec.Drop)
    $level = 24 + $spec.Drop
    $left = 24 - $half
    $step = 2 * $half / $spec.Waves
    for ($i = 0; $i -lt $spec.Waves; $i++) {
        $start = $left + $i * $step
        $sign = if ($i % 2 -eq 0) { -1 } else { 1 }
        [pscustomobject]@{
            X0 = $start
            C1 = $start + $step / 3
            C2 = $start + 2 * $step / 3
            X1 = $start + $step
            Y = $level
            ControlY = $level + $sign * $spec.Amplitude * 4 / 3
        }
    }
}

function New-WaterPath($spec) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    foreach ($s in Get-WaterSegments $spec) {
        $path.AddBezier([single]$s.X0, [single]$s.Y, [single]$s.C1, [single]$s.ControlY, [single]$s.C2, [single]$s.ControlY, [single]$s.X1, [single]$s.Y)
    }

    $radius = $spec.WaterRadius
    $theta = [Math]::Asin($spec.Drop / $radius) * 180 / [Math]::PI
    $path.AddArc([single](24 - $radius), [single](24 - $radius), [single](2 * $radius), [single](2 * $radius), [single]$theta, [single](180 - 2 * $theta))
    $path.CloseFigure()
    return $path
}

function New-TilePath {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $corner = 21
    $path.AddArc(0, 0, $corner, $corner, 180, 90)
    $path.AddArc(48 - $corner, 0, $corner, $corner, 270, 90)
    $path.AddArc(48 - $corner, 48 - $corner, $corner, $corner, 0, 90)
    $path.AddArc(0, 48 - $corner, $corner, $corner, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-LogoBitmap([int]$size) {
    $spec = Get-LogoSpec $size
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = 'AntiAlias'
        $graphics.PixelOffsetMode = 'HighQuality'
        $graphics.CompositingQuality = 'HighQuality'
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.ScaleTransform($size / 48.0, $size / 48.0)
        $graphics.FillPath((New-Object System.Drawing.SolidBrush $tileColor), (New-TilePath))
        $pen = New-Object System.Drawing.Pen $ringColor, ([single]$spec.RingWidth)
        $diameter = [single](2 * $spec.RingRadius)
        $graphics.DrawEllipse($pen, [single](24 - $spec.RingRadius), [single](24 - $spec.RingRadius), $diameter, $diameter)
        $graphics.FillPath((New-Object System.Drawing.SolidBrush $waterColor), (New-WaterPath $spec))
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Get-PngBytes([int]$size) {
    $bitmap = New-LogoBitmap $size
    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    finally {
        $bitmap.Dispose()
    }
}

function Write-Icon([string]$path) {
    # PNG-compressed entries for every size Windows chooses between at 100-200% scaling.
    $sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
    $frames = New-Object 'System.Collections.Generic.List[byte[]]'
    foreach ($size in $sizes) {
        [byte[]]$frame = Get-PngBytes $size
        $frames.Add($frame)
    }

    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }

    foreach ($frame in $frames) {
        $writer.Write($frame)
    }

    $writer.Flush()
    [System.IO.File]::WriteAllBytes($path, $stream.ToArray())
}

function Write-Svg([string]$path) {
    $spec = Get-LogoSpec 1024
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $n = { param($value) ([double]$value).ToString('0.###', $culture) }
    $segments = @(Get-WaterSegments $spec)
    $data = "M$(& $n $segments[0].X0) $(& $n $segments[0].Y)"
    foreach ($s in $segments) {
        $data += " C$(& $n $s.C1) $(& $n $s.ControlY) $(& $n $s.C2) $(& $n $s.ControlY) $(& $n $s.X1) $(& $n $s.Y)"
    }

    $data += " A$(& $n $spec.WaterRadius) $(& $n $spec.WaterRadius) 0 0 1 $(& $n $segments[0].X0) $(& $n $segments[0].Y)Z"
    $svg = @(
        '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 48 48" role="img" aria-label="UsageAI">'
        '  <rect width="48" height="48" rx="10.5" fill="#1f1f1f"/>'
        "  <circle cx=""24"" cy=""24"" r=""$(& $n $spec.RingRadius)"" fill=""none"" stroke=""#f1f1ef"" stroke-width=""$(& $n $spec.RingWidth)""/>"
        "  <path d=""$data"" fill=""#60cdff""/>"
        '</svg>'
        ''
    ) -join "`n"
    [System.IO.File]::WriteAllText($path, $svg, (New-Object System.Text.UTF8Encoding $false))
}

function Write-ReviewSheet([string]$path) {
    $sheet = New-Object System.Drawing.Bitmap 760, 330
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    try {
        $graphics.Clear([System.Drawing.Color]::FromArgb(45, 47, 51))
        $graphics.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(32, 32, 32))), 290, 20, 450, 130)
        $graphics.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(243, 243, 243))), 290, 170, 450, 130)
        $x = 300
        foreach ($size in 16, 20, 24, 32, 40, 48, 64) {
            $icon = New-LogoBitmap $size
            $graphics.DrawImageUnscaled($icon, $x, 85 - [int]($size / 2))
            $graphics.DrawImageUnscaled($icon, $x, 235 - [int]($size / 2))
            $icon.Dispose()
            $x += $size + 14
        }

        $large = New-LogoBitmap 256
        $graphics.DrawImageUnscaled($large, 20, 37)
        $large.Dispose()
    }
    finally {
        $graphics.Dispose()
    }

    $sheet.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $sheet.Dispose()
}

$resources = Join-Path $RepositoryRoot 'Resources'
Write-Icon (Join-Path $resources 'app.ico')
Write-Svg (Join-Path $resources 'logo.svg')
$logo = New-LogoBitmap 1024
try {
    $logo.Save((Join-Path $resources 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    $logo.Save((Join-Path $RepositoryRoot 'extension\media\logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $logo.Dispose()
}

if ($ReviewSheet) {
    Write-ReviewSheet $ReviewSheet
}
