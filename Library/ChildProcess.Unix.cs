using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Text;

namespace System.TBA;

public static partial class ChildProcess
{
    private static ProcessOutput GetProcessOutputCore(SafeChildProcessHandle processHandle, SafeFileHandle readStdOut, SafeFileHandle readStdErr, TimeoutHelper timeout)
    {
        throw new NotImplementedException();
    }
}
