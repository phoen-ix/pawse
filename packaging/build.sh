#!/usr/bin/env sh
# Build ALL THREE Pawse installers. Run from this folder.
# Usage:  ./build.sh <version>    e.g.  ./build.sh 0.1.4
# Needs the .NET 11 SDK and makensis (Linux: apt install nsis). The same steps as
# release.yml's build job, which is what actually ships.
set -e
[ -n "$1" ] || { echo "Usage: ./build.sh <version>  (e.g. ./build.sh 0.1.4)"; exit 1; }

proj=../src/Pawse/Pawse.csproj

# Full: self-contained, needs nothing installed.
dotnet publish "$proj" -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version="$1" -o ../publish-full
# Minimal: framework-dependent, needs the .NET 11 Desktop Runtime.
dotnet publish "$proj" -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:DebugType=none -p:Version="$1" -o ../publish-min

cp ../publish-full/Pawse.exe Pawse.exe
cp ../publish-min/Pawse.exe Pawse-min.exe

makensis -WX -V2 -DVERSION="$1" pawse.nsi
makensis -WX -V2 -DVERSION="$1" -DFULL_ONLY pawse.nsi
makensis -WX -V2 -DVERSION="$1" -DMINIMAL_ONLY pawse.nsi
echo "Built Pawse-Setup-$1.exe, Pawse-Setup-$1-full.exe and Pawse-Setup-$1-min.exe"
