@echo off
chcp 65001 >nul
title OpenMU Server

echo [start-openmu] Killing old dotnet processes...
taskkill /f /im dotnet.exe 2>nul

echo [start-openmu] Waiting for ports to release...
timeout /t 3 /nobreak >nul

echo [start-openmu] Starting OpenMU server with AI Player support...
cd /d "E:\mu_ai\MU_VER_1\SERVERS\OPENMU"

dotnet run --project src/Startup/MUnique.OpenMU.Startup.csproj -- -autostart -demo -resolveIP:local

if errorlevel 1 (
    echo [start-openmu] Server exited with error code %errorlevel%
    pause
)
