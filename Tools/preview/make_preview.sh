#!/usr/bin/env bash
# Renders a preview screenshot of Sanguine City from a live server session.
#   Tools/preview/make_preview.sh [out.png] [yaw] [pitch] [dist] [dpr]
# Needs: Mono (mcs/mono), Node.js 18+, Playwright with Chromium.
set -euo pipefail
cd "$(dirname "$0")"
REPO=../..
[ -d node_modules/three ] || npm install --no-audit --no-fund
mkdir -p bin
mcs -langversion:7 -main:ExportScene -out:bin/export.exe -r:System.dll -r:System.Core.dll \
    $(find $REPO/Assets/Sanguine/Scripts/Core -name '*.cs') $REPO/Server/Tests/IntegrationTest.cs ExportScene.cs
mono bin/export.exe scene.json
node shoot.mjs "${1:-preview.png}" "${2:-auto}" "${3:-14}" "${4:-8}" "${5:-1}"
