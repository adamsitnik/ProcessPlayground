// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Kernel32
    {
#if NETFRAMEWORK
        [DllImport(Libraries.Kernel32, SetLastError = true)]
#else
        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
#endif
        internal static
#if NETFRAMEWORK
        extern
#else
        partial
#endif
        IntPtr CreateJobObjectW(IntPtr lpJobAttributes, IntPtr lpName);

#if NETFRAMEWORK
        [DllImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
#else
        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
#endif
        internal static
#if NETFRAMEWORK
        extern
#else
        partial
#endif
        bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
    }
}
