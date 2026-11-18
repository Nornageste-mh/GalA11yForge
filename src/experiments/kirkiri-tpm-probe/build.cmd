@echo off
rem task-44: build 32-bit KiriKiri probe TPM (x86 DLL exporting undecorated V2Link).
rem Usage: build.cmd [tjs]   (tjs = 追加 TJS 就绪探测，仅 R1 通过后使用)
setlocal
set SRC=%~dp0
set SRC=%SRC:~0,-1%
if "%VCVARS%"=="" set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat
set OUTNAME=kkr_probe
if /i "%~1"=="tjs" set OUTNAME=kkr_probe_tjs
call "%VCVARS%" x86 >nul || exit /b 1
if /i not "%VSCMD_ARG_TGT_ARCH%"=="x86" ( echo [x] wrong arch & exit /b 1 )

set CL=/nologo /EHsc /MT /O1 /DNDEBUG /DUNICODE /D_UNICODE /utf-8
if /i "%~1"=="tjs" set CL=%CL% /DKKR_PROBE_TJS=1
cl /c %CL% /Fo"%SRC%\%OUTNAME%.obj" "%SRC%\kkr_probe.cpp"
if errorlevel 1 exit /b 1
link /nologo /DLL /MACHINE:X86 /DEF:"%SRC%\kkr_probe.def" /OUT:"%SRC%\%OUTNAME%.dll" "%SRC%\%OUTNAME%.obj"
if errorlevel 1 exit /b 1
echo BUILD_OK %OUTNAME%.dll
