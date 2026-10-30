#!/usr/bin/env bash
# Builds VramMeter.exe and its setup, VramMeterSetup.exe, into publish/. Runs on macOS or Linux as well as Windows
# (Git Bash): needs the .NET 10 SDK and NSIS (brew install makensis / winget install NSIS.NSIS).
#   installer/build.sh            the version in src/VramMeter.csproj
set -euo pipefail
cd "$(dirname "$0")/.."
VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/VramMeter.csproj)
dotnet publish src -c Release -o publish -p:EnableWindowsTargeting=true -nologo -v q
LC_ALL=en_US.UTF-8 makensis -INPUTCHARSET UTF8 -V2 -DVERSION="$VERSION" -DEXE=../publish/VramMeter.exe installer/VramMeter.nsi
mv installer/VramMeterSetup.exe publish/
ls -l publish/VramMeter.exe publish/VramMeterSetup.exe
