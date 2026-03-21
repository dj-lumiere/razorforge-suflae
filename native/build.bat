@echo off
setlocal

echo Building RazorForge Native Libraries...

if not exist build mkdir build
cd build

echo Configuring with CMake...
cmake .. -G "Ninja" -DCMAKE_C_COMPILER="C:/Program Files/LLVM/bin/clang.exe" -DCMAKE_CXX_COMPILER="C:/Program Files/LLVM/bin/clang++.exe" -DCMAKE_BUILD_TYPE=Release

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

REM Also try Release subfolder in case using Visual Studio generator
copy lib\Release\*.dll ..\..\bin\Debug\net10.0\ 2>nul
copy lib\Release\*.dll ..\..\bin\Release\net10.0\ 2>nul
copy bin\Release\*.dll ..\..\bin\Debug\net10.0\ 2>nul
copy bin\Release\*.dll ..\..\bin\Release\net10.0\ 2>nul

echo Native libraries built successfully!