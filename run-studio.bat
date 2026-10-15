@echo off
cd /d "%~dp0"

:: Ensure DOTNET_ROOT points to valid .NET runtime installation
if exist "%~dp0.dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%~dp0.dotnet"
) else if exist "%ProgramFiles%\dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%ProgramFiles%\dotnet"
)

:: Remove stray hostfxr/hostpolicy dlls that trick AppHost into thinking it is a broken self-contained install
if exist "%~dp0publish\Studio\hostfxr.dll" del /f /q "%~dp0publish\Studio\hostfxr.dll"
if exist "%~dp0publish\Studio\hostpolicy.dll" del /f /q "%~dp0publish\Studio\hostpolicy.dll"

start "" "%~dp0publish\Studio\Companion.Studio.exe"
