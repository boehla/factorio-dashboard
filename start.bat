@echo off
setlocal
cd /d "%~dp0"
title Factorio Dashboard

echo ============================================
echo   Factorio Dashboard
echo   Server-RCON:  siehe src\Fdash.Api\appsettings.json
echo   Dashboard:    http://localhost:5000
echo ============================================
echo.

REM Browser nach kurzer Anlaufzeit oeffnen (Server + Collector brauchen ein paar Sekunden)
start "" cmd /c "timeout /t 5 >nul & start http://localhost:5000"

REM 1) Bevorzugt die fertig veroeffentlichte EXE (kein .NET SDK noetig)
if exist "%~dp0publish\Fdash.Api.exe" (
  echo Starte veroeffentlichte Version ^(publish\Fdash.Api.exe^) ...
  "%~dp0publish\Fdash.Api.exe"
  goto :end
)

REM 2) Fallback: ueber das .NET SDK starten (baut bei Bedarf)
where dotnet >nul 2>nul
if %errorlevel%==0 (
  echo Keine publish-EXE gefunden - starte ueber dotnet run ...
  echo ^(Fuer eine schnellere, SDK-freie Version einmalig publish.bat ausfuehren.^)
  echo.
  dotnet run --project "%~dp0src\Fdash.Api\Fdash.Api.csproj" -c Release
  goto :end
)

echo.
echo FEHLER: Weder publish\Fdash.Api.exe noch das .NET SDK gefunden.
echo   - Einmalig eine EXE bauen:   publish.bat
echo   - ODER das .NET 9 SDK installieren:  https://dotnet.microsoft.com/download/dotnet/9.0
echo.
pause

:end
echo.
echo Dashboard beendet.
pause
endlocal
