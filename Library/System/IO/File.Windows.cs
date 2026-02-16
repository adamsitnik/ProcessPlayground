using System;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace System.IO;

public static partial class FileExtensions
{
    private static SafeFileHandle OpenNullFileHandleCore()
    {
        return File.OpenHandle("NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.None);
    }

    private static void CreatePipeCore(out SafeFileHandle read, out SafeFileHandle write, bool asyncRead, bool asyncWrite)
    {
        Interop.Kernel32.SECURITY_ATTRIBUTES securityAttributes = default;

        // When neither end is async, use the simple CreatePipe API
        if (!asyncRead && !asyncWrite)
        {
            bool ret = Interop.Kernel32.CreatePipe(out read, out write, ref securityAttributes, 0);
            if (!ret || read.IsInvalid || write.IsInvalid)
            {
                throw new Win32Exception();
            }
            return;
        }

        // When one or both ends are async, use named pipes to support async I/O.
        string pipeName = $@"\\.\pipe\{Guid.NewGuid()}";

        // Security: we don't need to specify a security descriptor, because
        // we allow only for 1 instance of the pipe and immediately open the write end,
        // so there is no time window for another process to open the pipe with different permissions.
        // Even if that happens, we are going to fail to open the write end and throw an exception, so there is no security risk.

        // Determine the open mode for the read end
        int openMode = Interop.Kernel32.FileOperations.PIPE_ACCESS_INBOUND |
                       Interop.Kernel32.FileOperations.FILE_FLAG_FIRST_PIPE_INSTANCE; // Only one can be created with this name
        
        if (asyncRead)
        {
            openMode |= Interop.Kernel32.FileOperations.FILE_FLAG_OVERLAPPED; // Asynchronous I/O
        }

        int pipeMode = Interop.Kernel32.FileOperations.PIPE_TYPE_BYTE | // the alternative would be to use "Message"
                       Interop.Kernel32.FileOperations.PIPE_READMODE_BYTE | // Data is read from the pipe as a stream of bytes
                       Interop.Kernel32.FileOperations.PIPE_WAIT; // Blocking mode is enabled (the operations are not completed until there is data to read)

        // We could consider specyfing a larger buffer size.
        read = Interop.Kernel32.CreateNamedPipe(pipeName, openMode, pipeMode, 1, 0, 0, 0, ref securityAttributes);

        if (read.IsInvalid)
        {
            throw new Win32Exception();
        }

        try
        {
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
