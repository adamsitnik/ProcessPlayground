// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Kernel32
    {
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct STARTUPINFOEX
        {
            internal STARTUPINFO StartupInfo;
            internal LPPROC_THREAD_ATTRIBUTE_LIST lpAttributeList;
        }

        internal unsafe struct LPPROC_THREAD_ATTRIBUTE_LIST
        {
            internal void* AttributeList;
        }
    }
}
