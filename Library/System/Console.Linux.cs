using Microsoft.Win32.SafeHandles;

namespace System;

public static partial class ConsoleExtensions
{
    private static SafeFileHandle GetStdHandle(int handleType)
        => handleType switch
        {
            Interop.Kernel32.HandleTypes.STD_INPUT_HANDLE => new(0, false),
            Interop.Kernel32.HandleTypes.STD_OUTPUT_HANDLE => new(1, false),
            Interop.Kernel32.HandleTypes.STD_ERROR_HANDLE => new(2, false),
            _ => throw new ArgumentOutOfRangeException(nameof(handleType), "Invalid standard handle type.")
        };
}
