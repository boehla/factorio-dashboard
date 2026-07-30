<#
.SYNOPSIS
  Erzeugt mod/fdash-exporter/thumbnail.png aus einem Quellbild.

.DESCRIPTION
  Das Mod-Portal zeigt das Thumbnail als 144x144 PNG. Quellbilder kommen
  typischerweise als grosses JPG aus einem Bildgenerator — das laesst sich nicht
  direkt ausliefern: JPG akzeptiert Factorio nicht, und ein 1024er PNG waere
  groesser als der ganze restliche Mod.

  Verkleinert wird schrittweise (immer maximal halbieren, dann der letzte
  Schritt). Ein einzelner Sprung von 1024 auf 144 laesst duenne Linien —
  hier die Chart-Kurve und die Fensterrahmen — ausfransen; die Halbierungen
  mitteln stattdessen sauber herunter.

.PARAMETER Source
  Quellbild. Default: mod/assets/thumbnail-source.jpg

.PARAMETER Size
  Kantenlaenge des Ergebnisses. Default 144 (Portal-Vorgabe).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\mod\make-thumbnail.ps1
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "assets\thumbnail-source.jpg"),
    [int]$Size = 144
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }
$target = Join-Path $PSScriptRoot "fdash-exporter\thumbnail.png"

$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
try {
    if ($src.Width -ne $src.Height) {
        Write-Warning "Source is $($src.Width)x$($src.Height), not square - it will be squashed. Crop it first."
    }

    # Zwischenschritte: solange halbieren, wie mehr als Faktor 2 fehlt.
    $current = New-Object System.Drawing.Bitmap $src
    while ($current.Width -gt $Size * 2) {
        $w = [Math]::Max($Size, [int]($current.Width / 2))
        $h = [Math]::Max($Size, [int]($current.Height / 2))
        $next = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($next)
        try {
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $g.DrawImage($current, 0, 0, $w, $h)
        } finally { $g.Dispose() }
        $current.Dispose()
        $current = $next
    }

    $final = New-Object System.Drawing.Bitmap $Size, $Size
    $g = [System.Drawing.Graphics]::FromImage($final)
    try {
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $g.DrawImage($current, 0, 0, $Size, $Size)
    } finally { $g.Dispose() }

    $final.Save($target, [System.Drawing.Imaging.ImageFormat]::Png)
    $final.Dispose()
    $current.Dispose()
} finally {
    $src.Dispose()
}

$kb = [math]::Round((Get-Item $target).Length / 1KB, 1)
Write-Host "Wrote $target ($Size x $Size, $kb KB)"
