using System;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace System.IO;

public static partial class FileExtensions
{
#if NETFRAMEWORK
    private static int GetLastPInvokeError() => Marshal.GetLastWin32Error();
#else
    private static int GetLastPInvokeError() => GetLastPInvokeError();
#endif

    private static unsafe SafeFileHandle OpenNullFileHandleCore()
    {
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributes = default;

        SafeFileHandle handle = Interop.Kernel32.CreateFile(
            "NUL",
            Interop.Kernel32.GenericOperations.GENERIC_WRITE | Interop.Kernel32.GenericOperations.GENERIC_READ,
            FileShare.ReadWrite,
            &securityAttributes,
            FileMode.Open,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(GetLastPInvokeError(), "Failed to open NUL device");
        }

        return handle;
    }

    private static void CreateAnonymousPipeCore(out SafeFileHandle read, out SafeFileHandle write)
    {
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributesParent = default;

        bool ret = Interop.Kernel32.CreatePipe(out read, out write, ref securityAttributesParent, 0);
        if (!ret || read.IsInvalid || write.IsInvalid)
        {
            throw new Win32Exception(GetLastPInvokeError());
        }
    }

    private static void CreateNamedPipeCore(out SafeFileHandle read, out SafeFileHandle write, string? name)
    {
        string pipeName = $@"\\.\pipe\{name ?? Guid.NewGuid().ToString()}";
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributesParent = default;
        // TODO: think about security attributes
        // Example: current-user: https://github.com/dotnet/runtime/blob/ed58e5fd2d5bce794c1d5acafa9f268151fefd47/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Windows.cs#L102-L123

        const int openMode =
            Interop.Kernel32.FileOperations.PIPE_ACCESS_INBOUND |
            Interop.Kernel32.FileOperations.FILE_FLAG_FIRST_PIPE_INSTANCE | // Only one can be created with this name
            Interop.Kernel32.FileOperations.FILE_FLAG_OVERLAPPED; // Asynchronous I/O

        int pipeMode = Interop.Kernel32.FileOperations.PIPE_TYPE_BYTE | // the alternative would be to use "Message"
            Interop.Kernel32.FileOperations.PIPE_READMODE_BYTE | // Data is read from the pipe as a stream of bytes
            Interop.Kernel32.FileOperations.PIPE_WAIT; // Blocking mode is enabled (the operations are not completed until there is data to read)

        // TODO: handle pipe name collisions (very unlikely)
        read = Interop.Kernel32.CreateNamedPipe(pipeName, openMode, pipeMode, 1, 0, 0, 0, ref securityAttributesParent);

        if (read.IsInvalid)
        {
            throw new Win32Exception(GetLastPInvokeError());
        }

        try
        {
            // STD OUT and ERR can't use async IO
#if NETFRAMEWORK
            write = new FileStream(pipeName, FileMode.Open, FileAccess.Write, FileShare.Read).SafeFileHandle;
#else
            write = File.OpenHandle(pipeName, FileMode.Open, FileAccess.Write, FileShare.Read, FileOptions.None);
#endif
        }
        catch
        {
            read.Dispose();

            throw;
        }
    }
}
