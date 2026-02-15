#!/bin/bash
set -e

# Script to build a multi-platform NuGet package for ProcessPlayground.Library
# This script should be run on a machine that has all necessary build tools installed
# NOTE: Native libraries for Linux and macOS must be pre-built on their respective platforms

echo "Building ProcessPlayground.Library NuGet package for multiple platforms..."
echo ""

# Navigate to Library directory
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR/Library"

# Clean previous builds
echo "Cleaning previous builds..."
dotnet clean -c Release
rm -rf bin/Release obj/Release
echo ""

# Check if we're on a Unix platform and build native library
if [ "$(uname)" != "Windows_NT" ]; then
    echo "Building native library for $(uname)..."
    mkdir -p native/build
    cd native/build
    cmake ..
    cmake --build .
    cd ../..
    echo ""
fi

# Build for the current platform (no RID) first - this is required for dotnet pack
echo "Building for current platform (framework-dependent)..."
dotnet build -c Release
echo ""

# Build for each runtime separately
echo "Building for Windows (win-x64)..."
dotnet build -c Release -r win-x64 -p:SkipNativeBuild=true
echo ""

echo "Building for Linux (linux-x64)..."
if [ "$(uname)" = "Linux" ]; then
    dotnet build -c Release -r linux-x64
else
    # If not on Linux, skip native build (native library should be pre-placed)
    dotnet build -c Release -r linux-x64 -p:SkipNativeBuild=true
fi
echo ""

echo "Building for macOS (osx-x64)..."
if [ "$(uname)" = "Darwin" ]; then
    dotnet build -c Release -r osx-x64
else
    # If not on macOS, skip native build (native library should be pre-placed)
    dotnet build -c Release -r osx-x64 -p:SkipNativeBuild=true
fi
echo ""

# Create NuGet package that includes all runtime builds
echo "Creating NuGet package..."
dotnet pack -c Release -p:NoBuild=true -o "$SCRIPT_DIR/artifacts"
echo ""

echo "============================================"
echo "Package created successfully!"
echo "============================================"
echo ""
echo "Location: $SCRIPT_DIR/artifacts/ProcessPlayground.Library.*.nupkg"
echo ""
echo "IMPORTANT: To create a complete multi-platform package:"
echo "1. Ensure native libraries are built:"
echo "   - On Linux: Library/native/build/libpal_process.so"
echo "   - On macOS: Library/native/build/libpal_process.dylib"
echo "2. Native libraries will be included automatically if they exist"
echo ""
echo "To publish to NuGet.org:"
echo "  dotnet nuget push $SCRIPT_DIR/artifacts/ProcessPlayground.Library.*.nupkg \\"
echo "    --api-key YOUR_API_KEY \\"
echo "    --source https://api.nuget.org/v3/index.json"
