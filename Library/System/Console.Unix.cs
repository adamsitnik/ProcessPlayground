using Microsoft.Win32.SafeHandles;

namespace System;

public static partial class ConsoleExtensions
{
    private static SafeFileHandle GetStdHandle(int handleType)
        => handleType switch
        {
            Interop.Kernel32.HandleTypes.STD_INPUT_HANDLE => new((IntPtr)0, false),
            Interop.Kernel32.HandleTypes.STD_OUTPUT_HANDLE => new((IntPtr)1, false),
            Interop.Kernel32.HandleTypes.STD_ERROR_HANDLE => new((IntPtr)2, false),
            _ => throw new ArgumentOutOfRangeException(nameof(handleType), "Invalid standard handle type.")
        };
}
