@echo off
rem task-45: build 32-bit KiriKiri plugin PROXY dll (x86, undecorated V2Link/V2Unlink/GetModuleThreadModel).
rem The built kkr_dllproxy.dll is deployed as plugin\<target>.dll; at runtime it loads <target>_orig.dll.
setlocal
set SRC=%~dp0
set SRC=%SRC:~0,-1%
if "%VCVARS%"=="" set VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat
call "%VCVARS%" x86 >nul || exit /b 1
if /i not "%VSCMD_ARG_TGT_ARCH%"=="x86" ( echo [x] wrong arch & exit /b 1 )

set CL=/nologo /EHsc /MT /O1 /DNDEBUG /DUNICODE /D_UNICODE /utf-8
cl /c %CL% /Fo"%SRC%\kkr_dllproxy.obj" "%SRC%\kkr_dllproxy.cpp"
if errorlevel 1 exit /b 1
link /nologo /DLL /MACHINE:X86 /DEF:"%SRC%\kkr_dllproxy.def" /OUT:"%SRC%\kkr_dllproxy.dll" "%SRC%\kkr_dllproxy.obj"
if errorlevel 1 exit /b 1
echo BUILD_OK kkr_dllproxy.dll
