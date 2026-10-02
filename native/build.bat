@echo off
rem 编译 ytdlp_host.dll：优先 MSVC (cl)，回退到 MSYS2/MinGW (gcc)
setlocal
cd /d "%~dp0"

where cl >nul 2>nul
if %errorlevel%==0 goto :msvc

for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2^>nul`) do set "VSPATH=%%i"
if defined VSPATH (
    call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
    goto :msvc
)

where gcc >nul 2>nul
if %errorlevel%==0 goto :mingw

echo [错误] 未找到 cl.exe 或 gcc.exe，请安装 VS Build Tools 或 MSYS2。
exit /b 1

:msvc
cl /nologo /W4 /O2 /GS /DYTDLP_HOST_BUILD /LD ytdlp_host.c /Fe:ytdlp_host.dll /Fo:ytdlp_host.obj /link /DLL /INCREMENTAL:NO
if %errorlevel%==0 del /q ytdlp_host.obj ytdlp_host.lib ytdlp_host.exp 2>nul
goto :done

:mingw
gcc -std=c11 -O2 -Wall -Wextra -shared -DYTDLP_HOST_BUILD -o ytdlp_host.dll ytdlp_host.c

:done
if exist ytdlp_host.dll (echo [完成] ytdlp_host.dll) else (echo [失败] 生成 DLL 失败 & exit /b 1)
endlocal
