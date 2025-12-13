using System.Runtime.InteropServices;

namespace System;

// Helper class for .NET Framework to provide OS detection methods
internal static class OperatingSystemHelper
{
    public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    
    public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    
    public static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
}

#if NETFRAMEWORK
// Polyfill extension for .NET Framework to add OperatingSystem.IsWindows() etc. methods
public static partial class OperatingSystemExtensions
{
    extension(OperatingSystem)
    {
        public static bool IsWindows() => OperatingSystemHelper.IsWindows();
        
        public static bool IsMacOS() => OperatingSystemHelper.IsMacOS();
        
        public static bool IsLinux() => OperatingSystemHelper.IsLinux();
    }
}
#endif
