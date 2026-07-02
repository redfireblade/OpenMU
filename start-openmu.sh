#!/bin/bash
# OpenMU Server Start Script — kills stale processes and cleans logs before starting

echo "[start-openmu] Killing old dotnet processes..."
/c/Windows/System32/taskkill.exe //F //IM dotnet.exe 2>/dev/null
echo "[start-openmu] Waiting for ports to release..."
sleep 2

echo "[start-openmu] Cleaning old logs..."
/c/Windows/System32/cmd.exe //c "for /r E:\mu_ai\MU_VER_1\SERVERS\OPENMU\src\Startup\logs %f in (log*.txt) do @del /Q %f 2>nul" 2>/dev/null
/c/Windows/System32/cmd.exe //c "for /r E:\mu_ai\MU_VER_1\SERVERS\OPENMU\src\Startup\bin\Debug\logs %f in (log*.txt) do @del /Q %f 2>nul" 2>/dev/null

echo "[start-openmu] Starting OpenMU server..."
cd "E:/mu_ai/MU_VER_1/SERVERS/OPENMU"
exec "/c/Program Files/dotnet/dotnet.exe" run --project src/Startup/MUnique.OpenMU.Startup.csproj -- -autostart -resolveIp:local
