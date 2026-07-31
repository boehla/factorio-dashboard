# Baut Frontend + veröffentlicht die API als self-contained Windows-EXE (F15).
# Aufruf:  powershell -ExecutionPolicy Bypass -File .\publish.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "1/2  Frontend bauen..." -ForegroundColor Cyan
Push-Location "$root\web"
npm install
npm run build          # Output -> src\Fdash.Api\wwwroot
Pop-Location

Write-Host "2/2  API veröffentlichen (win-x64, self-contained)..." -ForegroundColor Cyan

# wwwroot vorher leeren: dotnet publish ueberschreibt zwar, loescht aber nichts.
# Vite vergibt pro Build neue Hash-Dateinamen, deshalb sammeln sich in einem
# wiederverwendeten publish-Ordner sonst alle je gebauten Bundles an (lokal
# schnell mehrere MB Leichen, die niemand mehr referenziert). Nur wwwroot, damit
# eine bereits angelegte fdash.db daneben unangetastet bleibt.
if (Test-Path "$root\publish\wwwroot") { Remove-Item "$root\publish\wwwroot" -Recurse -Force }
dotnet publish "$root\src\Fdash.Api\Fdash.Api.csproj" -c Release -r win-x64 --self-contained true `
    /p:PublishSingleFile=true -o "$root\publish"

Write-Host "Fertig. Start:  .\publish\Fdash.Api.exe" -ForegroundColor Green
Write-Host "Dashboard:      http://localhost:5000"
