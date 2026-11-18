@echo off
rem task-47 / R26: kkz_mvp.dll (getSample.dll proxy + TJS hook + capture)
rem /EHa is mandatory (task-46: /EHsc crashed on TJS class definition).
setlocal
set SRC=%~dp0
set SRC=%SRC:~0,-1%
if "%VCVARS%"=="" set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat
call "%VCVARS%" x86 >nul || exit /b 1
if /i not "%VSCMD_ARG_TGT_ARCH%"=="x86" ( echo [x] wrong arch & exit /b 1 )

set CL=/nologo /EHa /MT /O1 /DNDEBUG /DUNICODE /D_UNICODE /utf-8
cl /c %CL% /Fo"%SRC%\kkz_mvp.obj" "%SRC%\kkz_mvp.cpp"
if errorlevel 1 exit /b 1
link /nologo /DLL /MACHINE:X86 /DEF:"%SRC%\kkz_mvp.def" /MAP:"%SRC%\kkz_mvp.map" /OUT:"%SRC%\kkz_mvp.dll" "%SRC%\kkz_mvp.obj"
if errorlevel 1 exit /b 1
echo BUILD_OK kkz_mvp.dll
