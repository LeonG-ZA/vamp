#!/usr/bin/env bash
# Builds the dedicated server and the integration test with the Mono C# compiler.
# (Alternatively use `dotnet build Server/SanguineServer.csproj`.)
set -euo pipefail
cd "$(dirname "$0")"
CORE=$(find ../Assets/Sanguine/Scripts/Core -name '*.cs' | sort)
mkdir -p bin
mcs -langversion:7 -optimize+ -out:bin/SanguineServer.exe -r:System.dll $CORE Program.cs
mcs -langversion:7 -debug -out:bin/SanguineTests.exe -r:System.dll $CORE Tests/IntegrationTest.cs
echo "Built bin/SanguineServer.exe and bin/SanguineTests.exe"
