using System;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace System.IO;

// All the new methods would go directly to File in dotnet/runtime,
// but here the best I can do is extension members.
public static partial class FileExtensions
{
    // DESIGN: we should either keep all these methods return inheritable handles,
    // or create non-inheritable ALWAYS and just duplicate for Process.Start-like APIs.
    extension(File)
    {
        /// <summary>
        /// Opens a handle to the null device (`NUL` on Windows, `/dev/null` on Unix).
        /// </summary>
        /// <remarks>
        /// <para>Null device discards all data written to it and provides no data when read from.</para>
        /// <para>It can be used for discarding process output or starting a process with no standard input.</para>
        /// </remarks>
        public static SafeFileHandle OpenNullFileHandle() => OpenNullFileHandleCore();

        /// <summary>
        /// Creates an anonymous pipe for inter-process communication.
        /// </summary>
        /// <remarks>
        /// <para>The read end of the pipe can be used to read data written to the write end.</para>
        /// </remarks>
        public static void CreateAnonymousPipe(out SafeFileHandle read, out SafeFileHandle write)
            => CreateAnonymousPipeCore(out read, out write);

        /// <summary>
        /// Creates a named pipe for inter-process communication.
        /// </summary>
        /// <remarks>
        /// <para>The read end of the pipe can be used to read data written to the write end.</para>
        /// <para>The read end is a async and write end is sync.</para>
        /// </remarks>
        // DESIGN: this method may be too specific to make it public in general File API!
        public static void CreateNamedPipe(out SafeFileHandle read, out SafeFileHandle write, string? name = null)
            => CreateNamedPipeCore(out read, out write, name);
    }
}
