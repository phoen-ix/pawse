@echo off
rem Build ALL THREE Pawse installers. Run from this folder.
rem Usage:  build.bat <version>    e.g.  build.bat 0.1.4
rem Needs the .NET 10 SDK and NSIS (makensis on PATH). The same steps as release.ymls
rem build job, which is what actually ships.
setlocal
if "%~1"=="" (
  echo Usage: build.bat ^<version^>   e.g.  build.bat 0.1.4
  exit /b 1
)
set PROJ=..\src\Pawse\Pawse.csproj

rem Full: self-contained, needs nothing installed.
dotnet publish %PROJ% -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=%~1 -o ..\publish-full || exit /b 1
rem Minimal: framework-dependent, needs the .NET 10 Desktop Runtime.
dotnet publish %PROJ% -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:Version=%~1 -o ..\publish-min || exit /b 1

copy /y "..\publish-full\Pawse.exe" "Pawse.exe" >nul || exit /b 1
copy /y "..\publish-min\Pawse.exe" "Pawse-min.exe" >nul || exit /b 1

makensis /WX /V2 /DVERSION=%~1 pawse.nsi || exit /b 1
makensis /WX /V2 /DVERSION=%~1 /DFULL_ONLY pawse.nsi || exit /b 1
makensis /WX /V2 /DVERSION=%~1 /DMINIMAL_ONLY pawse.nsi || exit /b 1

echo.
echo Built Pawse-Setup-%~1.exe, Pawse-Setup-%~1-full.exe and Pawse-Setup-%~1-min.exe
