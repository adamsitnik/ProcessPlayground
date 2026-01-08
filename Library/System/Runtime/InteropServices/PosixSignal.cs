// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NETFRAMEWORK

namespace System.Runtime.InteropServices;

/// <summary>
/// Represents POSIX signals that can be sent to a process.
/// </summary>
/// <remarks>
/// This is a polyfill for .NET Framework. The actual enum is available in .NET 6.0+.
/// The values match those defined in the BCL.
/// </remarks>
public enum PosixSignal
{
    /// <summary>Hangup detected on controlling terminal or death of controlling process.</summary>
    SIGHUP = -1,
    
    /// <summary>Interrupt from keyboard (Ctrl+C).</summary>
    SIGINT = -2,
    
    /// <summary>Quit from keyboard (Ctrl+\).</summary>
    SIGQUIT = -3,
    
    /// <summary>Termination signal.</summary>
    SIGTERM = -4,
    
    /// <summary>Child stopped or terminated.</summary>
    SIGCHLD = -5,
    
    /// <summary>Continue if stopped.</summary>
    SIGCONT = -6,
    
    /// <summary>Window resize signal.</summary>
    SIGWINCH = -7,
    
    /// <summary>Terminal input for background process.</summary>
    SIGTTIN = -8,
    
    /// <summary>Terminal output for background process.</summary>
    SIGTTOU = -9,
    
    /// <summary>Stop typed at terminal (Ctrl+Z).</summary>
    SIGTSTP = -10,
}

#endif
