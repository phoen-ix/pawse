#!/usr/bin/env sh
# Build BOTH Pawse installers. Run from this folder after placing the release exes
# (Pawse.exe and Pawse-min.exe) here.  Usage: ./build.sh <version>  e.g. ./build.sh 0.1.4
set -e
[ -n "$1" ] || { echo "Usage: ./build.sh <version>  (e.g. ./build.sh 0.1.4)"; exit 1; }
makensis -DVERSION="$1" pawse.nsi
makensis -DVERSION="$1" -DMINIMAL_ONLY pawse.nsi
echo "Built Pawse-Setup-$1.exe and Pawse-Setup-$1-min.exe"
