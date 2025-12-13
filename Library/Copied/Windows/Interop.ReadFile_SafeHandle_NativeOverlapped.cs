// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using System.Threading;

internal static partial class Interop
{
    internal static partial class Kernel32
    {
#if NETFRAMEWORK
        [DllImport(Libraries.Kernel32, SetLastError = true)]
        internal static unsafe extern
#else
        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
        internal static unsafe partial
#endif
         int ReadFile(
            SafeHandle handle,
            byte* bytes,
            int numBytesToRead,
            IntPtr numBytesRead_mustBeZero,
            NativeOverlapped* overlapped);

#if NETFRAMEWORK
        [DllImport(Libraries.Kernel32, SetLastError = true)]
        internal static unsafe extern
#else
        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
        internal static unsafe partial
#endif
         int ReadFile(
            SafeHandle handle,
            byte* bytes,
            int numBytesToRead,
            out int numBytesRead,
            NativeOverlapped* overlapped);
    }
}
