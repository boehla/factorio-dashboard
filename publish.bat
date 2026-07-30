@echo off
REM Baut Frontend + veroeffentlicht die API als self-contained Windows-EXE (F15).
setlocal
set ROOT=%~dp0
echo 1/2  Frontend bauen...
pushd "%ROOT%web" || exit /b 1
call npm install || exit /b 1
call npm run build || exit /b 1
popd
echo 2/2  API veroeffentlichen (win-x64, self-contained)...
  dotnet publish "%ROOT%src\Fdash.Api\Fdash.Api.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishSqlitePCLRawBundle=true /p:IncludeNativeLibrariesForSelfExtract=true -o "%ROOT%publish" || exit /b 1
echo Fertig. Start:  publish\Fdash.Api.exe   Dashboard: http://localhost:5000
endlocal
