<#
.SYNOPSIS
  Packt fdash-exporter zu einer Mod-Portal-tauglichen ZIP.

.DESCRIPTION
  Factorio erwartet im ZIP genau einen Ordner auf oberster Ebene, benannt
  <name>_<version>. Die Version wird aus info.json gelesen, damit sie nur an
  einer Stelle gepflegt werden muss.

.PARAMETER Install
  Kopiert die ZIP zusaetzlich in das Mod-Verzeichnis und entfernt dort aeltere
  Versionen desselben Mods.

.PARAMETER ModsDir
  Zielverzeichnis fuer -Install. Ohne Angabe %APPDATA%\Factorio\mods. Eine
  portable Installation (config-path.cfg mit
  use-system-read-write-data-directories=false) legt ihre Mods dagegen unter
  dem Installationsordner ab, z.B. C:\Data\Factorio\mods.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\mod\build-mod.ps1 -Install

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\mod\build-mod.ps1 -Install -ModsDir C:\Data\Factorio\mods
#>
param(
    [switch]$Install,
    [string]$ModsDir
)

$ErrorActionPreference = "Stop"

$modRoot = Join-Path $PSScriptRoot "fdash-exporter"
$infoPath = Join-Path $modRoot "info.json"
if (-not (Test-Path $infoPath)) { throw "info.json not found at $infoPath" }

$info = Get-Content $infoPath -Raw | ConvertFrom-Json
$name = $info.name
$version = $info.version
$folder = "${name}_${version}"

$distDir = Join-Path $PSScriptRoot "dist"
# Eine Ebene Zwischenverzeichnis, damit im ZIP genau ein Ordner oben liegt.
$stageRoot = Join-Path $distDir "_stage"
$stageDir = Join-Path $stageRoot $folder
$zipPath = Join-Path $distDir "$folder.zip"

if (Test-Path $stageRoot) { Remove-Item -Recurse -Force $stageRoot }
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

# Nur die Dateien, die ins Release gehoeren. Alles andere (Notizen, .bak, ...)
# bleibt aussen vor, damit die ZIP reproduzierbar bleibt.
$include = @("info.json", "settings.lua", "control.lua", "changelog.txt", "LICENSE", "README.md", "thumbnail.png")
foreach ($f in $include) {
    $src = Join-Path $modRoot $f
    if (Test-Path $src) { Copy-Item $src -Destination $stageDir }
}
foreach ($d in @("scripts", "locale")) {
    $src = Join-Path $modRoot $d
    if (Test-Path $src) { Copy-Item $src -Destination $stageDir -Recurse }
}

# Eintraege bewusst von Hand anlegen. Weder Compress-Archive noch
# ZipFile::CreateFromDirectory taugen hier: beide schreiben unter Windows
# PowerShell 5.1 (.NET Framework) Backslashes als Pfadtrenner in die
# ZIP-Eintragsnamen. Die ZIP-Spezifikation verlangt "/", und ein Reader, der
# sich daran haelt, sieht dann nur Dateien mit merkwuerdigen Namen statt
# scripts/-Unterordner.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $prefixLength = $stageRoot.Length + 1
    foreach ($item in Get-ChildItem -Path $stageRoot -Recurse -File) {
        $entryName = $item.FullName.Substring($prefixLength).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $item.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally {
    $zip.Dispose()
}
Remove-Item -Recurse -Force $stageRoot

$size = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)
Write-Host "Built $zipPath ($size KB)"

if ($Install) {
    if ($ModsDir) { $targetDir = $ModsDir } else { $targetDir = Join-Path $env:APPDATA "Factorio\mods" }
    if (-not (Test-Path $targetDir)) { throw "Factorio mods directory not found: $targetDir (use -ModsDir for a portable install)" }
    Get-ChildItem $targetDir -Filter "${name}_*.zip" | Remove-Item -Force
    Copy-Item $zipPath -Destination $targetDir -Force
    Write-Host "Installed to $targetDir"
    Write-Host "Restart Factorio (or the headless server) to load it."
}
