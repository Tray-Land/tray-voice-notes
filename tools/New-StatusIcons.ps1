<#
.SYNOPSIS
    Renders the recording-state tray icons (Assets\Recording.ico, Assets\Paused.ico).
    Red reads on both light and dark taskbars, so each state needs only one icon.
#>
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\Assets'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function New-Frame([int]$size, [string]$state) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(1, $size / 16.0)
    $d = $size - 2 * $inset
    $red = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 232, 17, 35))
    $g.FillEllipse($red, $inset, $inset, $d, $d)

    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    if ($state -eq 'recording') {
        # The universal "record" mark: a white dot in the red disc.
        $dot = $d * 0.40
        $g.FillEllipse($white, ($size - $dot) / 2, ($size - $dot) / 2, $dot, $dot)
    } else {
        # Pause bars.
        $h = $d * 0.44; $w = [Math]::Max(1.5, $d * 0.13); $gap = [Math]::Max(1.5, $d * 0.12)
        $top = ($size - $h) / 2
        $g.FillRectangle($white, $size / 2 - $gap / 2 - $w, $top, $w, $h)
        $g.FillRectangle($white, $size / 2 + $gap / 2, $top, $w, $h)
    }

    $g.Dispose()
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [string]$state) {
    $frames = foreach ($s in $sizes) { , (New-Frame $s $state) }
    $out = [System.IO.BinaryWriter]::new([System.IO.File]::Create($path))
    $out.Write([uint16]0); $out.Write([uint16]1); $out.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $frames[$i].Length
        $out.Write([byte]($s -band 0xFF)); $out.Write([byte]($s -band 0xFF))
        $out.Write([byte]0); $out.Write([byte]0)
        $out.Write([uint16]1); $out.Write([uint16]32)
        $out.Write([uint32]$len); $out.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($f in $frames) { $out.Write($f) }
    $out.Dispose()
}

Write-Ico (Join-Path $assets 'Recording.ico') 'recording'
Write-Ico (Join-Path $assets 'Paused.ico') 'paused'
Write-Host "Wrote Recording.ico and Paused.ico"
