// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal static partial class Interop
{
    internal static partial class Kernel32
    {
#if NETFRAMEWORK
        [DllImport(Libraries.Kernel32, SetLastError = true)]
#else
        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
#endif
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe
#if NETFRAMEWORK
        extern
#else
        partial
#endif
        bool GetOverlappedResult(
            SafeFileHandle hFile,
            NativeOverlapped* lpOverlapped,
            ref int lpNumberOfBytesTransferred,
            [MarshalAs(UnmanagedType.Bool)] bool bWait);
    }
}
