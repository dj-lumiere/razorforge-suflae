@echo off
setlocal

echo Building RazorForge Native Libraries...

if not exist build mkdir build
cd build

echo Configuring with CMake...

REM Prefer the clang on PATH (CI installs LLVM 20 there; matches the toolchain
REM the compiler itself shells out to). Fall back to the default LLVM install
REM location for dev machines whose PATH lacks clang.
set "RF_CLANG=clang"
set "RF_CLANGXX=clang++"
where clang >nul 2>nul
if %ERRORLEVEL% neq 0 (
    set "RF_CLANG=C:/Program Files/LLVM/bin/clang.exe"
    set "RF_CLANGXX=C:/Program Files/LLVM/bin/clang++.exe"
)

cmake .. -G "Ninja" -DCMAKE_C_COMPILER="%RF_CLANG%" -DCMAKE_CXX_COMPILER="%RF_CLANGXX%" -DCMAKE_BUILD_TYPE=Release

if %ERRORLEVEL% neq 0 (
    echo CMake configuration failed!
    exit /b 1
)

echo Building libraries...
cmake --build . --config Release

if %ERRORLEVEL% neq 0 (
    echo Build failed!
    exit /b 1
)

echo Copying libraries to project directories...
if not exist ..\..\bin\Debug\net10.0 mkdir ..\..\bin\Debug\net10.0
if not exist ..\..\bin\Release\net10.0 mkdir ..\..\bin\Release\net10.0

REM With Ninja generator, output is directly in lib\ and bin\ (no Release subfolder)
copy lib\*.dll ..\..\bin\Debug\net10.0\ 2>nul
copy lib\*.dll ..\..\bin\Release\net10.0\ 2>nul
copy bin\*.dll ..\..\bin\Debug\net10.0\ 2>nul
copy bin\*.dll ..\..\bin\Release\net10.0\ 2>nul

REM Shared libs built by vendored subprojects land in their own subdirs
REM (bdwgc emits gc.dll into bdwgc\) — the runtime links them dynamically,
REM so they must sit next to razorforge_runtime.dll.
copy bdwgc\*.dll ..\..\bin\Debug\net10.0\ 2>nul
copy bdwgc\*.dll ..\..\bin\Release\net10.0\ 2>nul

REM Also try Release subfolder in case using Visual Studio generator
copy lib\Release\*.dll ..\..\bin\Debug\net10.0\ 2>nul
copy lib\Release\*.dll ..\..\bin\Release\net10.0\ 2>nul
copy bin\Release\*.dll ..\..\bin\Debug\net10.0\ 2>nul
copy bin\Release\*.dll ..\..\bin\Release\net10.0\ 2>nul

echo Native libraries built successfully!