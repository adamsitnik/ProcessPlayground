// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;

namespace System.TBA;

/// <summary>Specifies a POSIX signal number.</summary>
public enum PosixSignal
{
    /// <summary>Hangup</summary>
    SIGHUP = -1,

    /// <summary>Interrupt</summary>
    SIGINT = -2,

    /// <summary>Quit</summary>
    SIGQUIT = -3,

    /// <summary>Termination</summary>
    SIGTERM = -4,

    /// <summary>Child stopped</summary>
    [UnsupportedOSPlatform("windows")]
    SIGCHLD = -5,

    /// <summary>Continue if stopped</summary>
    [UnsupportedOSPlatform("windows")]
    SIGCONT = -6,

    /// <summary>Window resized</summary>
    [UnsupportedOSPlatform("windows")]
    SIGWINCH = -7,

    /// <summary>Terminal input for background process</summary>
    [UnsupportedOSPlatform("windows")]
    SIGTTIN = -8,

    /// <summary>Terminal output for background process</summary>
    [UnsupportedOSPlatform("windows")]
    SIGTTOU = -9,

    /// <summary>Stop typed at terminal</summary>
    [UnsupportedOSPlatform("windows")]
    SIGTSTP = -10,

    /// <summary>Force kill (cannot be caught/ignored)</summary>
    [UnsupportedOSPlatform("windows")]
    SIGKILL = -11,

    /// <summary>Abort</summary>
    SIGABRT = -12,

    /// <summary>User-defined signal 1</summary>
    [UnsupportedOSPlatform("windows")]
    SIGUSR1 = -13,

    /// <summary>User-defined signal 2</summary>
    [UnsupportedOSPlatform("windows")]
    SIGUSR2 = -14,

    /// <summary>Broken pipe</summary>
    [UnsupportedOSPlatform("windows")]
    SIGPIPE = -15,

    /// <summary>Alarm clock</summary>
    [UnsupportedOSPlatform("windows")]
    SIGALRM = -16,

    /// <summary>Stop (cannot be caught/ignored)</summary>
    [UnsupportedOSPlatform("windows")]
    SIGSTOP = -17,
}
