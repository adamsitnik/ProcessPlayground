# Building and Publishing the Multi-Platform NuGet Package

This document explains how to build and publish the ProcessPlayground.Library NuGet package with support for Windows, Linux, and macOS.

## Overview

The ProcessPlayground.Library package is designed to work across multiple platforms with platform-specific implementations:
- **Windows (win-x64)**: Windows-specific process management code
- **Linux (linux-x64)**: Linux-specific code with native library (`libpal_process.so`)
- **macOS (osx-x64)**: macOS-specific code with native library (`libpal_process.dylib`)

## Prerequisites

### All Platforms
- .NET 10.0 SDK (or compatible version)

### For Building Native Libraries (Linux/macOS only)
- CMake 3.20 or higher
- C compiler (GCC or Clang)
- pthread library

**Note:** Native libraries for Linux and macOS must be built on their respective platforms. You cannot cross-compile the native libraries.

## Building Strategy

Due to the native library requirements, you have two options for creating the NuGet package:

### Option 1: Multi-Machine Build (Recommended)

This approach builds the native libraries on each platform and then combines them into a single package.

#### Step 1: Build on Linux
```bash
cd Library
dotnet build -c Release -r linux-x64
# This will build the C# code AND the native library (libpal_process.so)
```

Copy the built native library from `Library/native/build/libpal_process.so` to a shared location.

#### Step 2: Build on macOS
```bash
cd Library
dotnet build -c Release -r osx-x64
# This will build the C# code AND the native library (libpal_process.dylib)
```

Copy the built native library from `Library/native/build/libpal_process.dylib` to a shared location.

#### Step 3: Build on Windows (or any platform)
```bash
cd Library
dotnet build -c Release -r win-x64
# This will build the C# code for Windows (no native library needed)
```

#### Step 4: Prepare for Packaging
Copy the native libraries to the appropriate locations in the Library directory:
```bash
# Ensure the native libraries exist at these paths before packing:
Library/native/build/libpal_process.so    # For Linux
Library/native/build/libpal_process.dylib # For macOS
```

#### Step 5: Create the NuGet Package
```bash
cd Library
dotnet pack -c Release -o ../artifacts
```

This will create a NuGet package in the `artifacts` directory containing:
- Windows binaries in `lib/net10.0/`
- Linux binaries in `runtimes/linux-x64/lib/net10.0/`
- macOS binaries in `runtimes/osx-x64/lib/net10.0/`
- Linux native library in `runtimes/linux-x64/native/`
- macOS native library in `runtimes/osx-x64/native/`

### Option 2: Single-Platform Build with Pre-built Natives

If you only have access to one platform (e.g., Linux), you can:

1. Build the C# code for all platforms
2. Build the native library on your platform
3. Obtain pre-built native libraries from other platforms
4. Manually place them in the correct locations before packing

#### Build on Linux
```bash
# Build C# code for all platforms
cd Library
dotnet build -c Release -r win-x64
dotnet build -c Release -r linux-x64  # Also builds libpal_process.so
dotnet build -c Release -r osx-x64

# The linux-x64 build will create libpal_process.so automatically
# For macOS, you need to obtain libpal_process.dylib from a macOS build
# and place it at: Library/native/build/libpal_process.dylib

# Then create the package
dotnet pack -c Release -o ../artifacts
```

## Using the Build Scripts

For convenience, build scripts are provided that automate the build process:

### Linux/macOS
```bash
chmod +x build-package.sh
./build-package.sh
```

### Windows (PowerShell)
```powershell
.\build-package.ps1
```

**Important:** These scripts will attempt to build for all platforms, but native libraries will only be built on their respective platforms. You may need to manually provide the native libraries for platforms you're not building on.

## Verifying the Package

After building the package, you can inspect its contents:

```bash
# Extract the .nupkg file (it's a ZIP archive)
unzip -l artifacts/ProcessPlayground.Library.0.1.0.nupkg

# Or use nuget.exe
nuget list -Source ./artifacts
```

Verify that the package contains:
- `lib/net10.0/ProcessPlayground.Library.dll` (or platform-specific locations)
- `runtimes/linux-x64/native/libpal_process.so`
- `runtimes/osx-x64/native/libpal_process.dylib`
- `runtimes/win-x64/lib/net10.0/ProcessPlayground.Library.dll`
- `runtimes/linux-x64/lib/net10.0/ProcessPlayground.Library.dll`
- `runtimes/osx-x64/lib/net10.0/ProcessPlayground.Library.dll`

## Publishing to NuGet.org

Once the package is built and verified:

1. Create an account on [NuGet.org](https://www.nuget.org/)
2. Generate an API key in your account settings
3. Publish the package:

```bash
dotnet nuget push artifacts/ProcessPlayground.Library.0.1.0.nupkg \
  --api-key YOUR_API_KEY \
  --source https://api.nuget.org/v3/index.json
```

## Troubleshooting

### Native Library Not Built
If the native library is not being built, ensure:
- You're on a Unix-based platform (Linux or macOS)
- CMake is installed and in your PATH
- The build tools (gcc/clang) are available

You can skip the native build temporarily using:
```bash
dotnet build -c Release -r linux-x64 -p:SkipNativeBuild=true
```

### Package Missing Native Libraries
The native libraries are only included in the package if they exist at build time. Make sure:
- The native libraries are built before running `dotnet pack`
- The files exist at `Library/native/build/libpal_process.so` and `Library/native/build/libpal_process.dylib`

### Cross-Platform Build Issues
Since the native libraries cannot be cross-compiled, you must build them on their respective platforms. Consider using:
- CI/CD pipelines (GitHub Actions, Azure DevOps) to build on multiple platforms
- Docker containers for Linux builds
- Virtual machines for macOS builds

## CI/CD Integration

For automated building on multiple platforms, see the GitHub Actions workflow in `.github/workflows/dotnet.yml`. You can extend it to:
1. Build on each platform (Windows, Linux, macOS)
2. Upload artifacts from each build
3. Download all artifacts in a final job
4. Create and publish the NuGet package

Example GitHub Actions job to create the package:
```yaml
package:
  needs: [build-windows, build-linux, build-macos]
  runs-on: ubuntu-latest
  steps:
    - uses: actions/checkout@v4
    - name: Download Linux artifacts
      uses: actions/download-artifact@v4
      with:
        name: linux-native
        path: Library/native/build/
    - name: Download macOS artifacts
      uses: actions/download-artifact@v4
      with:
        name: macos-native
        path: Library/native/build/
    - name: Build package
      run: |
        cd Library
        dotnet build -c Release -r win-x64
        dotnet build -c Release -r linux-x64 -p:SkipNativeBuild=true
        dotnet build -c Release -r osx-x64 -p:SkipNativeBuild=true
        dotnet pack -c Release -o ../artifacts
    - name: Publish to NuGet
      run: dotnet nuget push artifacts/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json
```

## Version Management

To update the package version, edit `Library/Library.csproj`:
```xml
<Version>0.2.0</Version>
```

Follow [Semantic Versioning](https://semver.org/) guidelines for version numbers.
