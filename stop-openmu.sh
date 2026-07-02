#!/bin/bash
# OpenMU Server Stop Script — kills all dotnet processes

echo "[stop-openmu] Killing all dotnet processes..."
/c/Windows/System32/taskkill.exe //F //IM dotnet.exe 2>/dev/null
echo "[stop-openmu] Done."
