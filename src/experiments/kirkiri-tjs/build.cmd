@echo off
rem Build 32-bit KiriKiri Z TPM experiment.
rem Requires VS2022 vcvars32.bat (set VCVARS to override).
setlocal
set SRC=%~dp0
set SRC=%SRC:~0,-1%
set TJS=%SRC%\..\..\..\_kkr-lab\engine-src\tjs2
if "%VCVARS%"=="" set VCVARS=D:\VS2022\VC\Auxiliary\Build\vcvars32.bat
call "%VCVARS%" >nul || exit /b 1
if /i not "%VSCMD_ARG_TGT_ARCH%"=="x86" ( echo [x] wrong arch & exit /b 1 )
set CL=/nologo /EHsc /MT /O1 /DNDEBUG /D_CRT_SECURE_NO_WARNINGS /DTJS_NO_REGEXP /DUNICODE /D_UNICODE /D__WIN32__ /utf-8
cl /c %CL% /I"%SRC%" /I"%TJS%" /FI"%SRC%\force.h" /Fo"%SRC%\gal_a11y.obj" "%SRC%\gal_a11y.cpp"
if errorlevel 1 exit /b 1
link /nologo /DLL /MACHINE:X86 /DEF:"%SRC%\gal_a11y.def" /OUT:"%SRC%\gal_a11y.dll" "%SRC%\gal_a11y.obj" user32.lib
if errorlevel 1 exit /b 1
echo BUILD_OK
