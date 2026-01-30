# Script to build a multi-platform NuGet package for ProcessPlayground.Library
# This script should be run on a machine that has all necessary build tools installed
# NOTE: Native libraries for Linux and macOS must be pre-built on their respective platforms

Write-Host "Building ProcessPlayground.Library NuGet package for multiple platforms..." -ForegroundColor Green
Write-Host ""

# Navigate to Library directory
Set-Location "$PSScriptRoot\Library"

# Clean previous builds
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
dotnet clean -c Release
Remove-Item -Path "bin\Release" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "obj\Release" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""

# Build for the current platform (no RID) first - this is required for dotnet pack
Write-Host "Building for current platform (framework-dependent)..." -ForegroundColor Cyan
dotnet build -c Release
Write-Host ""

# Build for each runtime separately
Write-Host "Building for Windows (win-x64)..." -ForegroundColor Cyan
dotnet build -c Release -r win-x64 -p:SkipNativeBuild=true
Write-Host ""

Write-Host "Building for Linux (linux-x64)..." -ForegroundColor Cyan
# Skip native build on Windows (native library should be pre-placed)
dotnet build -c Release -r linux-x64 -p:SkipNativeBuild=true
Write-Host ""

Write-Host "Building for macOS (osx-x64)..." -ForegroundColor Cyan
# Skip native build on Windows (native library should be pre-placed)
dotnet build -c Release -r osx-x64 -p:SkipNativeBuild=true
Write-Host ""

# Create NuGet package that includes all runtime builds
Write-Host "Creating NuGet package..." -ForegroundColor Cyan
dotnet pack -c Release -p:NoBuild=true -o "$PSScriptRoot\artifacts"
Write-Host ""

Write-Host "============================================" -ForegroundColor Green
Write-Host "Package created successfully!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "Location: $PSScriptRoot\artifacts\ProcessPlayground.Library.*.nupkg" -ForegroundColor White
Write-Host ""
Write-Host "IMPORTANT: To create a complete multi-platform package:" -ForegroundColor Yellow
Write-Host "1. Ensure native libraries are built:" -ForegroundColor Yellow
Write-Host "   - On Linux: Library\native\build\libpal_process.so" -ForegroundColor Gray
Write-Host "   - On macOS: Library\native\build\libpal_process.dylib" -ForegroundColor Gray
Write-Host "2. Native libraries will be included automatically if they exist" -ForegroundColor Yellow
Write-Host ""
Write-Host "To publish to NuGet.org:" -ForegroundColor Yellow
Write-Host "  dotnet nuget push $PSScriptRoot\artifacts\ProcessPlayground.Library.*.nupkg ``" -ForegroundColor Gray
Write-Host "    --api-key YOUR_API_KEY ``" -ForegroundColor Gray
Write-Host "    --source https://api.nuget.org/v3/index.json" -ForegroundColor Gray
