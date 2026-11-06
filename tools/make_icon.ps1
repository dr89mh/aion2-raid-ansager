# Draws the tray/exe icon (speaker with sound waves) and writes assets\ansager.ico with PNG frames 16..256.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\assets\ansager.ico"
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $k = $s / 32.0
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 7 * $k; $w = $s - 1
    $bg.AddArc(0, 0, 2*$r, 2*$r, 180, 90); $bg.AddArc($w-2*$r, 0, 2*$r, 2*$r, 270, 90)
    $bg.AddArc($w-2*$r, $w-2*$r, 2*$r, 2*$r, 0, 90); $bg.AddArc(0, $w-2*$r, 2*$r, 2*$r, 90, 90); $bg.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 30, 44))), $bg)
    $pts = @(
        (New-Object System.Drawing.PointF (5*$k), (12*$k)), (New-Object System.Drawing.PointF (10*$k), (12*$k)),
        (New-Object System.Drawing.PointF (17*$k), (6*$k)), (New-Object System.Drawing.PointF (17*$k), (26*$k)),
        (New-Object System.Drawing.PointF (10*$k), (20*$k)), (New-Object System.Drawing.PointF (5*$k), (20*$k)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 176, 0))), [System.Drawing.PointF[]]$pts)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.2, 2.2*$k))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawArc($pen, 14*$k, 10*$k, 8*$k, 12*$k, -50, 100)
    if ($s -ge 24) { $g.DrawArc($pen, 14*$k, 6*$k, 14*$k, 20*$k, -50, 100) }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    ,$ms.ToArray()
}
$fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
Write-Output "geschrieben: $out"
