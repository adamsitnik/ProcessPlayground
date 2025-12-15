using System;

namespace System.Runtime.InteropServices;

// Polyfill for .NET Framework to provide Marshal.GetLastPInvokeError extension member
public static partial class MarshalExtensions
{
    extension(Marshal)
    {
        public static int GetLastPInvokeError() => Marshal.GetLastWin32Error();
    }
}
