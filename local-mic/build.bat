@echo off
setlocal

rem 构建 Windows 端 local-mic。
rem 产物是单个 exe，目标机只需要 .NET Framework 4.8（Windows 10 1809+ / Windows 11 自带）。

set "DOTNET=C:\Program Files\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=%USERPROFILE%\.workbuddy\binaries\dotnet-sdk\dotnet.exe"
if not exist "%DOTNET%" (
  echo 找不到 dotnet SDK。
  echo 请安装 .NET SDK 8（https://dotnet.microsoft.com/download），或把 dotnet.exe 路径写进本脚本。
  exit /b 1
)

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

"%DOTNET%" build "%~dp0local-mic.csproj" -c Release

if errorlevel 1 (
  echo 构建失败。
  exit /b 1
)

echo.
echo 产物：%~dp0bin\Release\local-mic.exe
endlocal
