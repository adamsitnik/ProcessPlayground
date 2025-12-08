using Microsoft.Win32.SafeHandles;

namespace Library;

public static partial class ConsoleExtensions
{
    private static SafeFileHandle GetStdHandle(int handleType) => new(Interop.Kernel32.GetStdHandle(handleType), false);
}
