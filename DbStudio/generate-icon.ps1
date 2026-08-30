param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$source = [System.Drawing.Image]::FromFile($InputPath)
try {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = @()

    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $graphics.DrawImage($source, 0, 0, $size, $size)
            }
            finally {
                $graphics.Dispose()
            }

            $memory = New-Object System.IO.MemoryStream
            try {
                $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames += [PSCustomObject]@{
                    Size = $size
                    Bytes = $memory.ToArray()
                }
            }
            finally {
                $memory.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }

    $directory = [System.IO.Path]::GetDirectoryName($OutputPath)
    if ($directory) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    $stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    try {
        $writer = New-Object System.IO.BinaryWriter($stream)
        try {
            # ICONDIR
            $writer.Write([UInt16]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]$frames.Count)

            $offset = 6 + (16 * $frames.Count)
            foreach ($frame in $frames) {
                $dimension = if ($frame.Size -ge 256) { [byte]0 } else { [byte]$frame.Size }
                $bytes = [byte[]]$frame.Bytes

                # ICONDIRENTRY
                $writer.Write($dimension)
                $writer.Write($dimension)
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([UInt16]1)
                $writer.Write([UInt16]32)
                $writer.Write([UInt32]$bytes.Length)
                $writer.Write([UInt32]$offset)
                $offset += $bytes.Length
            }

            foreach ($frame in $frames) {
                $writer.Write([byte[]]$frame.Bytes)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}
finally {
    $source.Dispose()
}
