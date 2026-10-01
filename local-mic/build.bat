@echo off
setlocal

rem 构建 Windows 端 local-mic。
rem 产物是单个 exe，目标机只需要 .NET Framework 4.8（Windows 10 1809+ / Windows 11 自带）。
rem
rem ⚠ 挑 dotnet 和判失败这两处都踩过坑，改之前先看 docs/HARDWARE.md §3.1：
rem   1) 挑 dotnet **不能只看文件在不在**。机器上常同时存在「只装了运行时、没装 SDK」的
rem      dotnet.exe —— 它 exists，从它构建只会得到 "No .NET SDKs were found"。
rem   2) 失败判定**不能写 if errorlevel 1**。那只在 errorlevel >= 1 时成立，而没有 SDK 的
rem      dotnet build 返回的是**负数**（0x80008073 / -2147450735），会被 1 >= 1 判成假
rem      ⇒ 失败被跳过，照常打印「产物：…」还 exit 0。必须用字符串比 "!=" 0"。

set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

call :pick_dotnet
if not defined DOTNET goto :no_sdk

echo 用到的 dotnet：%DOTNET%
"%DOTNET%" build "%~dp0local-mic.csproj" -c Release
if not "%ERRORLEVEL%"=="0" (
  echo 构建失败（dotnet 返回 %ERRORLEVEL%）。
  exit /b 1
)

echo.
echo 产物：%~dp0bin\Release\local-mic.exe
endlocal
exit /b 0

rem ---- 逐个候选探测「真的带 SDK」，第一个能用的胜出 ----
:pick_dotnet
set "DOTNET="
call :try_dotnet "C:\Program Files\dotnet\dotnet.exe"
if not defined DOTNET call :try_dotnet "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
if not defined DOTNET call :try_dotnet "%USERPROFILE%\.workbuddy\binaries\dotnet-sdk\dotnet.exe"
exit /b 0

rem ---- %1 = 候选 dotnet.exe ----
:try_dotnet
if not exist "%~1" exit /b 0
rem ⚠ 这里**只能判输出有没有行，不能判退出码**：没有 SDK 时 --list-sdks 同样返回 0，
rem    只是没有任何输出。反过来判就会挑中一个根本不能构建的 dotnet。
set "HAS_SDK="
for /f "delims=" %%S in ('"%~1" --list-sdks 2^>nul') do set "HAS_SDK=1"
if defined HAS_SDK set "DOTNET=%~1"
exit /b 0

:no_sdk
echo 找不到**可用的** .NET SDK（只装了 dotnet.exe 但没有 SDK 同样算找不到）。
echo 请安装 .NET SDK 8（https://dotnet.microsoft.com/download），或把路径写进上面的候选表。
echo.
echo 以下候选实测结果：
call :report "C:\Program Files\dotnet\dotnet.exe"
call :report "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
call :report "%USERPROFILE%\.workbuddy\binaries\dotnet-sdk\dotnet.exe"
endlocal
exit /b 1

:report
if not exist "%~1" goto :report_missing
set "HAS_SDK="
for /f "delims=" %%S in ('"%~1" --list-sdks 2^>nul') do set "HAS_SDK=1"
if defined HAS_SDK (echo    [有 SDK] %~1) else (echo    [无 SDK] %~1)
exit /b 0
:report_missing
echo    [不存在] %~1
exit /b 0
