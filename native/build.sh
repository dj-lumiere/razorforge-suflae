#!/bin/bash
set -e

echo "Building RazorForge Native Libraries..."

mkdir -p build
cd build

echo "Configuring with CMake..."
cmake .. -DCMAKE_BUILD_TYPE=Release

echo "Building libraries..."
cmake --build . --config Release

echo "Copying libraries to project directories..."
mkdir -p ../../bin/Debug/net10.0
mkdir -p ../../bin/Release/net10.0

# Copy shared libraries based on OS
if [[ "$OSTYPE" == "darwin"* ]]; then
    # macOS
    cp lib/*.dylib ../../bin/Debug/net10.0/ 2>/dev/null || true
    cp lib/*.dylib ../../bin/Release/net10.0/ 2>/dev/null || true
else
    # Linux
    cp lib/*.so ../../bin/Debug/net10.0/ 2>/dev/null || true
    cp lib/*.so ../../bin/Release/net10.0/ 2>/dev/null || true
fi

echo "Native libraries built successfully!"
