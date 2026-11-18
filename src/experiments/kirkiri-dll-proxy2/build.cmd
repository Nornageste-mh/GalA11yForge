@echo off
rem task-46 v3: staged TJS probe build. /EHa so SEH and C++ exceptions both land in catch(...).
rem /MAP lets us map a fault offset back to a function without guessing.
setlocal
set SRC=%~dp0
set SRC=%SRC:~0,-1%
if "%VCVARS%"=="" set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat
call "%VCVARS%" x86 >nul || exit /b 1
if /i not "%VSCMD_ARG_TGT_ARCH%"=="x86" ( echo [x] wrong arch & exit /b 1 )

set CL=/nologo /EHa /MT /O1 /DNDEBUG /DUNICODE /D_UNICODE /utf-8
cl /c %CL% /Fo"%SRC%\kkr_proxy2.obj" "%SRC%\kkr_proxy2.cpp"
if errorlevel 1 exit /b 1
link /nologo /DLL /MACHINE:X86 /DEF:"%SRC%\kkr_proxy2.def" /MAP:"%SRC%\kkr_proxy2.map" /OUT:"%SRC%\kkr_proxy2.dll" "%SRC%\kkr_proxy2.obj"
if errorlevel 1 exit /b 1
echo BUILD_OK kkr_proxy2.dll
