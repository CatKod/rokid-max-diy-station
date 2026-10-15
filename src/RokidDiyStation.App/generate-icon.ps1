# Generate app.ico for the tray icon
$ErrorActionPreference = 'Stop'
$icoPath = Join-Path $PSScriptRoot "obj\app.ico"
$bmp = New-Object System.Drawing.Bitmap(32, 32)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)
$g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255,0,150,180))),4,4,23,23)
$g.DrawEllipse((New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255,0,188,212),3)),1,1,29,29)
$sf = New-Object System.Drawing.StringFormat
$sf.Alignment = 'Center'
$sf.LineAlignment = 'Center'
$g.DrawString('R', (New-Object System.Drawing.Font('Segoe UI',14,[System.Drawing.FontStyle]::Bold)), (New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), (New-Object System.Drawing.RectangleF(0,0,32,32)), $sf)
$g.Dispose()
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([int16]0)
$bw.Write([int16]1)
$bw.Write([int16]1)
$bw.Write([byte]32)
$bw.Write([byte]32)
$bw.Write([byte]0)
$bw.Write([byte]0)
$bw.Write([int16]1)
$bw.Write([int16]32)
$ms2 = New-Object System.IO.MemoryStream
$bmp.Save($ms2, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $ms2.ToArray()
$bw.Write([int32]$pngBytes.Length)
$bw.Write([int32]22)
$bw.Write($pngBytes)
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bmp.Dispose()
Write-Host "Generated $icoPath"
