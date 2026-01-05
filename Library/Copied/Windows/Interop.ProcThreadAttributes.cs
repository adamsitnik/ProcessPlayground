// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Kernel32
    {
        internal const uint PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;

#if NETFRAMEWORK
        [DllImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
#else
        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
#endif
        internal static unsafe
#if NETFRAMEWORK
        extern
#else
        partial
#endif
        bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref nuint lpSize);

#if NETFRAMEWORK
        [DllImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
#else
        [LibraryImport(Libraries.Kernel32, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
#endif
        internal static unsafe
#if NETFRAMEWORK
        extern
#else
        partial
#endif
        bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            int dwFlags,
            nuint Attribute,
            IntPtr lpValue,
            nuint cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

#if NETFRAMEWORK
        [DllImport(Libraries.Kernel32)]
#else
        [LibraryImport(Libraries.Kernel32)]
#endif
        internal static
#if NETFRAMEWORK
        extern
#else
        partial
#endif
        void DeleteProcThreadAttributeList(IntPtr lpAttributeList);
    }
}
