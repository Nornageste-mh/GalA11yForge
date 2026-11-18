@echo off
rem ===========================================================================
rem task-51 / R28: kkz_proxy.dll -- KKZ (KiriKiri Z 1.2.x) adapter v1 proxy
rem Builds an x86 DLL exporting V2Link/V2Unlink/GetModuleThreadModel (undecorated).
rem /EHa is mandatory (TJS throws C++ exceptions across our frames).
rem .def must stay ASCII-only: Chinese comments there make link silently drop
rem every export (LNK4017), producing a "successful" but useless DLL.
rem ===========================================================================
setlocal
set SRC=%~dp0
set SRC=%SRC:~0,-1%
if not exist "%SRC%\build" mkdir "%SRC%\build"
if "%VCVARS%"=="" set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat
call "%VCVARS%" x86 >nul || exit /b 1
if /i not "%VSCMD_ARG_TGT_ARCH%"=="x86" ( echo [x] wrong arch & exit /b 1 )

set CL=/nologo /EHa /MT /O1 /DNDEBUG /DUNICODE /D_UNICODE /utf-8
cl /c %CL% /Fo"%SRC%\build\kkz_proxy.obj" "%SRC%\kkz_proxy.cpp"
if errorlevel 1 exit /b 1
link /nologo /DLL /MACHINE:X86 /DEF:"%SRC%\kkz_proxy.def" /MAP:"%SRC%\build\kkz_proxy.map" ^
  /OUT:"%SRC%\build\kkz_proxy.dll" "%SRC%\build\kkz_proxy.obj" version.lib user32.lib
if errorlevel 1 exit /b 1
echo BUILD_OK %SRC%\build\kkz_proxy.dll
