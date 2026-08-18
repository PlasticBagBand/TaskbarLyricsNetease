@echo off
rem 一键构建并运行（不暂停）
echo.|call "%~dp0build.bat" >nul 2>&1
if exist "%~dp0TaskbarLyricsNetease.exe" (
    start "" "%~dp0TaskbarLyricsNetease.exe"
    echo 已启动 TaskbarLyricsNetease
) else (
    echo 构建失败，请直接运行 build.bat 查看错误
    pause
)
