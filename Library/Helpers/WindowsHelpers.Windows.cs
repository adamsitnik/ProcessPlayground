using System.Runtime.InteropServices;

namespace System.TBA;

internal static partial class WindowsHelpers
{
    private const int MAX_PATH = 260;

#if NETFRAMEWORK
    [DllImport("kernel32.dll", EntryPoint = "GetSystemDirectoryW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetSystemDirectoryW(char[] lpBuffer, uint uSize);

    [DllImport("kernel32.dll", EntryPoint = "GetWindowsDirectoryW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetWindowsDirectoryW(char[] lpBuffer, uint uSize);
#else
    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetSystemDirectoryW(char[] lpBuffer, uint uSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetWindowsDirectoryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetWindowsDirectoryW(char[] lpBuffer, uint uSize);
#endif

    internal static string? GetSystemDirectory()
    {
        char[] buffer = new char[MAX_PATH];
        uint length = GetSystemDirectoryW(buffer, MAX_PATH);
        
        if (length == 0 || length > MAX_PATH)
        {
            return null;
        }

        return new string(buffer, 0, (int)length);
    }

    internal static string? GetWindowsDirectory()
    {
        char[] buffer = new char[MAX_PATH];
        uint length = GetWindowsDirectoryW(buffer, MAX_PATH);
        
        if (length == 0 || length > MAX_PATH)
        {
            return null;
        }

        return new string(buffer, 0, (int)length);
    }
}
