using Microsoft.Win32.SafeHandles;

namespace System;

// All the new methods would go directly to Console in dotnet/runtime,
// but here the best I can do is extension members.
public static partial class ConsoleExtensions
{
    extension(Console)
    {
        public static SafeFileHandle GetStandardInputHandle() => GetStdHandle(Interop.Kernel32.HandleTypes.STD_INPUT_HANDLE);

        public static SafeFileHandle GetStandardOutputHandle() => GetStdHandle(Interop.Kernel32.HandleTypes.STD_OUTPUT_HANDLE);

        public static SafeFileHandle GetStandardErrorHandle() => GetStdHandle(Interop.Kernel32.HandleTypes.STD_ERROR_HANDLE);
    }
}
