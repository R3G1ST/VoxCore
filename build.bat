@echo off
REM VoxCore build script - patches WinUI XAML compiler bug automatically

set PROJECT_DIR=%~dp0VoxCore.Client
set TOOLS_DIR=%USERPROFILE%\.nuget\packages\microsoft.windowsappsdk.winui\2.3.6\tools\net472

echo [1/3] Cleaning build output...
if exist "%PROJECT_DIR%\bin" rmdir /s /q "%PROJECT_DIR%\bin"
if exist "%PROJECT_DIR%\obj" rmdir /s /q "%PROJECT_DIR%\obj"

echo [2/3] Restoring packages and patching XAML compiler...
dotnet restore "%PROJECT_DIR%\VoxCore.Client.csproj"

if exist "%TOOLS_DIR%\ru-RU" (
    rmdir /s /q "%TOOLS_DIR%\ru-RU"
    echo   Patched: removed ru-RU
)

echo [3/3] Building...
dotnet build "%PROJECT_DIR%\VoxCore.Client.csproj" -p:Platform=x64 -c Release --no-restore

if %ERRORLEVEL% EQU 0 (
    echo.
    echo === BUILD SUCCEEDED ===
) else (
    echo.
    echo === BUILD FAILED ===
)

pause
