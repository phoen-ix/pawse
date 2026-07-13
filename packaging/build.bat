@echo off
rem Build BOTH Pawse installers. Run from this folder after placing the release
rem exes (Pawse.exe and Pawse-min.exe) here.  Usage:  build.bat <version>
rem Example:  build.bat 0.1.4
setlocal
if "%~1"=="" (
  echo Usage: build.bat ^<version^>   e.g.  build.bat 0.1.4
  exit /b 1
)
makensis /DVERSION=%~1 pawse.nsi || exit /b 1
makensis /DVERSION=%~1 /DMINIMAL_ONLY pawse.nsi || exit /b 1
echo.
echo Built Pawse-Setup-%~1.exe and Pawse-Setup-%~1-min.exe
