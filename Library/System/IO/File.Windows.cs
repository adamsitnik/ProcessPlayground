using System;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace System.IO;

public static partial class FileExtensions
{
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
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Failed to open NUL device");
        }

        return handle;
    }

    private static void CreatePipeCore(out SafeFileHandle read, out SafeFileHandle write, bool asyncRead, bool asyncWrite)
    {
        // When neither end is async, use the simple CreatePipe API
        if (!asyncRead && !asyncWrite)
        {
            Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributesParent = default;

            bool ret = Interop.Kernel32.CreatePipe(out read, out write, ref securityAttributesParent, 0);
            if (!ret || read.IsInvalid || write.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            return;
        }

        // When one or both ends are async, use named pipes to support async I/O
        string pipeName = $@"\\.\pipe\{Guid.NewGuid()}";
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributesParent = default;

        // Determine the open mode for the read end
        int openMode = Interop.Kernel32.FileOperations.PIPE_ACCESS_INBOUND |
                       Interop.Kernel32.FileOperations.FILE_FLAG_FIRST_PIPE_INSTANCE;
        
        if (asyncRead)
        {
            openMode |= Interop.Kernel32.FileOperations.FILE_FLAG_OVERLAPPED;
        }

        int pipeMode = Interop.Kernel32.FileOperations.PIPE_TYPE_BYTE |
                       Interop.Kernel32.FileOperations.PIPE_READMODE_BYTE |
                       Interop.Kernel32.FileOperations.PIPE_WAIT;

        read = Interop.Kernel32.CreateNamedPipe(pipeName, openMode, pipeMode, 1, 0, 0, 0, ref securityAttributesParent);

        if (read.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            // Open the write end with appropriate options
            FileOptions writeOptions = asyncWrite ? FileOptions.Asynchronous : FileOptions.None;
            write = File.OpenHandle(pipeName, FileMode.Open, FileAccess.Write, FileShare.Read, writeOptions);
        }
        catch
        {
            read.Dispose();
            throw;
        }
    }
}
