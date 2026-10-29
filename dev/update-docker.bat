@echo off
rem SPDX-License-Identifier: MIT
rem
rem Builds the app image and redeploys it to the dev stack (dev\dev.sh build + update-docker).
rem   dev\update-docker.bat            after bumping <version> and <image-tag> in appinfo\info.xml
rem   dev\update-docker.bat register   same version again: re-register instead (data volume is kept)
setlocal

rem Git Bash, not WSL's bash.exe in System32.
set "BASH=%ProgramFiles%\Git\bin\bash.exe"
if not exist "%BASH%" (
    for /f "delims=" %%G in ('where git 2^>nul') do if not defined GITDIR set "GITDIR=%%~dpG.."
    if defined GITDIR set "BASH=%GITDIR%\bin\bash.exe"
)
if not exist "%BASH%" (
    echo Git Bash not found. Install Git for Windows, or run dev/dev.sh from Git Bash.
    exit /b 1
)

set "DEPLOY=update-docker"
if /i "%~1"=="register" set "DEPLOY=register-docker"

"%BASH%" "%~dp0dev.sh" build || exit /b 1
"%BASH%" "%~dp0dev.sh" %DEPLOY% || exit /b 1
echo Deployed. Nextcloud: http://localhost:8180
