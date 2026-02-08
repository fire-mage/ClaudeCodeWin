Add-Type -AssemblyName System.Drawing

$srcPath = Join-Path $PSScriptRoot '..\src\ClaudeCodeWin\icon-1024.png'
$icoPath = Join-Path $PSScriptRoot '..\src\ClaudeCodeWin\app.ico'

$src = [System.Drawing.Image]::FromFile((Resolve-Path $srcPath).Path)
$sizes = @(16, 32, 48, 256)

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

# ICO header
$bw.Write([UInt16]0)            # Reserved
$bw.Write([UInt16]1)            # Type: ICO
$bw.Write([UInt16]$sizes.Count) # Image count

# Render each size to PNG bytes
$imageData = @()
foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()

    $pngStream = New-Object System.IO.MemoryStream
    $bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $pngStream.ToArray()
    $imageData += ,($bytes)
    $pngStream.Dispose()
    $bmp.Dispose()
}

# Calculate starting offset for image data
$offset = 6 + ($sizes.Count * 16)

# Write directory entries
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $data = $imageData[$i]
    $w = if ($size -ge 256) { 0 } else { $size }
    $h = if ($size -ge 256) { 0 } else { $size }

    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0)              # Color palette
    $bw.Write([byte]0)              # Reserved
    $bw.Write([UInt16]1)            # Color planes
    $bw.Write([UInt16]32)           # Bits per pixel
    $bw.Write([UInt32]$data.Length)  # Image data size
    $bw.Write([UInt32]$offset)      # Offset to image data

    $offset += $data.Length
}

# Write image data
foreach ($data in $imageData) {
    $bw.Write($data)
}

[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()
$src.Dispose()

Write-Host "ICO created: $icoPath ($((Get-Item $icoPath).Length) bytes)"
Write-Host "Sizes: $($sizes -join ', ')px"
