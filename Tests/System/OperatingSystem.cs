using System.Runtime.InteropServices;

namespace System;

// Polyfill extension for .NET Framework to add OperatingSystem.IsWindows() etc. methods
public static partial class OperatingSystemExtensions
{
    extension(OperatingSystem)
    {
        public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        
        public static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }
}
