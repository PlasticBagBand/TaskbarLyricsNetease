@echo off
rem ============================================================
rem  TaskbarLyricsNetease 构建脚本（用系统自带 .NET Framework 编译器）
rem  双击运行，生成的 TaskbarLyricsNetease.exe 在项目根目录
rem ============================================================
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo [错误] 找不到 csc.exe（需要 .NET Framework 4.x，Win10/11 自带）
    pause
    exit /b 1
)

cd /d "%~dp0"

"%CSC%" /nologo /target:exe /optimize+ /codepage:65001 ^
  /out:TaskbarLyricsNetease.exe ^
  /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll" ^
  /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll" ^
  /r:lib\Windows.WinMD ^
  /r:lib\Windows.Foundation.FoundationContract.winmd ^
  /r:lib\Windows.Foundation.UniversalApiContract.winmd ^
  /r:lib\Windows.Media.MediaControlContract.winmd ^
  src\TaskbarLyricsNetease.cs

if errorlevel 1 (
    echo.
    echo [错误] 编译失败，见上方信息
    pause
    exit /b 1
)

echo.
echo [完成] 已生成 TaskbarLyricsNetease.exe
echo   运行: TaskbarLyricsNetease.exe          （正常使用）
echo   自检: TaskbarLyricsNetease.exe --probe  （检查歌曲识别/歌词接口）
echo   调试: TaskbarLyricsNetease.exe --console
pause
