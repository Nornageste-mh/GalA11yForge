@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul || exit /b 1
cl /nologo /EHa /MT /O1 /DNDEBUG /DUNICODE /D_UNICODE /utf-8 /DKKZ_TEST_MAIN /Fo"build\sha_test.obj" /Fe"build\sha_test.exe" kkz_proxy.cpp /link user32.lib version.lib > build\sha_build.log 2>&1
exit /b %errorlevel%
