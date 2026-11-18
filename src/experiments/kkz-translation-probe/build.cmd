@echo off
rem task-48 / R27: kkz_trans.dll (getSample.dll proxy + TJS read-only probe + capture)
rem /EHa is mandatory (task-46: /EHsc crashed on TJS class definition).
setlocal
set SRC=%~dp0
set SRC=%SRC:~0,-1%
if "%VCVARS%"=="" set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat
call "%VCVARS%" x86 >nul || exit /b 1
if /i not "%VSCMD_ARG_TGT_ARCH%"=="x86" ( echo [x] wrong arch & exit /b 1 )

set CL=/nologo /EHa /MT /O1 /DNDEBUG /DUNICODE /D_UNICODE /utf-8
cl /c %CL% /Fo"%SRC%\kkz_trans.obj" "%SRC%\kkz_trans.cpp"
if errorlevel 1 exit /b 1
link /nologo /DLL /MACHINE:X86 /DEF:"%SRC%\kkz_trans.def" /MAP:"%SRC%\kkz_trans.map" /OUT:"%SRC%\kkz_trans.dll" "%SRC%\kkz_trans.obj" user32.lib
if errorlevel 1 exit /b 1
echo BUILD_OK kkz_trans.dll
