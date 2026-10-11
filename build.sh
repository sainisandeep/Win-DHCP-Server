#!/bin/bash
# ./build.sh        builds ./build/DHCPServer.exe (a single self-contained Windows program)
# ./build.sh test   runs the logic self-test instead
# Needs the .NET 10 SDK. Works on macOS, Linux and Windows (Git Bash).
set -euo pipefail
cd "$(dirname "$0")"

# Keep intermediate files out of the project folder.
ARTIFACTS="${HOME}/.cache/WinDHCPServer-build"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

if [[ "${1:-}" == "test" ]]; then
    exec dotnet run --project DhcpServer.SelfTest --artifacts-path "$ARTIFACTS"
fi

dotnet publish DhcpServer.App -c Release --artifacts-path "$ARTIFACTS" -o build -v quiet
echo "Built: $(pwd)/build/DHCPServer.exe"
