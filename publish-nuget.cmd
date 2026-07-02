@echo off
chcp 65001 >nul
title Publish Orchestrator NuGet

set PROJECT=src\Orchestrator\MUnique.OpenMU.Orchestrator.csproj
set CONFIG=Release

echo [nuget] Building Orchestrator in %CONFIG% mode...
dotnet build %PROJECT% --configuration %CONFIG%

echo [nuget] Creating NuGet package...
dotnet pack %PROJECT% --configuration %CONFIG% --include-source -p:SymbolPackageFormat=snupkg

echo [nuget] Package created. Output:
dir /b src\Orchestrator\bin\%CONFIG%\*.nupkg 2>nul
dir /b src\Orchestrator\bin\%CONFIG%\*.snupkg 2>nul

echo [nuget] Done.
pause
