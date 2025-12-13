// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;
using System.Threading;

#if NETFRAMEWORK
using LibraryImportAttribute = System.Runtime.InteropServices.DllImportAttribute;
#endif

internal static partial class Interop
{
    internal static partial class Kernel32
    {
        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe
#if NETFRAMEWORK
        extern
#else
        partial
#endif
        bool CancelIoEx(SafeHandle handle, NativeOverlapped* lpOverlapped);

        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe
#if NETFRAMEWORK
        extern
#else
        partial
#endif
        bool CancelIoEx(IntPtr handle, NativeOverlapped* lpOverlapped);
    }
}
