# ChildProcess Spec

This document describes the goals, design and the decisions made for the `ChildProcess` API.

`ChildProcess` is just a name for now. The actual namespace and class names may change. For now it's `System.TBA` (To Be Announced).

## The goals

Rewriting the process execution APIs in .NET is one in a lifetime opportunity to address long-standing pain points and improve usability, performance, and reliability. The main goals are:

1. Secure by design. Safe by default.
2. Cross-platform.
    1. Support every OS that .NET 10 supports.
    2. Make it possible for the community forks like FreeBSD to integrate.
3. Clear separation of concepts and responsibilities.
4. Synchronous and asynchronous execution paths are first‑class citizens: both must work equally well.
    1. All sychronous APIs support timeout.
    2. All asychronous APIs support cancellation.
5. Minimize the chance of deadlocks by design.
6. Performance:
    1. Synchronous APIs should not spawn background threads.
    2. Use native OS capabilities whenever possible.
7. Backward compatibility:.
    1. Avoid breaking changes to existing APIs.
    2. Make it possible to use both old and new APIs side by side.

## Overview of the API

The API is structured in layers to address different use cases:
- Low-level primitives for advanced scenarios (complete).
- Mid-level abstractions for common patterns (WIP).
- High-level convenience methods for common use cases (WIP).

### Option bag

`ProcessStartOptions` is an option bag class for configuring process creation. It is similar to `ProcessStartInfo`, but simpler yet more powerfull.

It aims at exposing only 100% cross-platform features.

The only missing features from all known user requests are the ability to set [priority](https://github.com/dotnet/runtime/issues/24890) and affinity (including processor [group](https://github.com/dotnet/runtime/issues/30124) including [NUMA](https://github.com/dotnet/runtime/issues/82220)).
I've not implemented them in my prototype, as I believe they are niche features that can be added later without breaking changes.

```csharp
namespace System.TBA;

public sealed class ProcessStartOptions
{
    public string FileName { get; }
    public IList<string> Arguments { get; }
    public IDictionary<string, string?> Environment { get; }
    public DirectoryInfo? WorkingDirectory { get; set; }

    public bool CreateSuspended { get; set; } // NEW (https://github.com/dotnet/runtime/issues/94127)
    public IList<SafeHandle> InheritedHandles { get; } // NEW (https://github.com/dotnet/runtime/issues/13943)
    public bool KillOnParentDeath { get; set; } // NEW (https://github.com/dotnet/runtime/issues/101985)

    public ProcessStartOptions(string fileName);

    /// <summary>
    /// Resolves the given file name to an absolute path and creates a new ProcessStartOptions instance.
    /// </summary>
    public static ProcessStartOptions ResolvePath(string fileName); // NEW
}
```

### Low-level APIs

#### SafeChildProcessHandle

A type that represents a handle to a child process. It provides low-level methods for child process management, including the ability to start the process with provided handles for STD IN/OUT/ERR.

The most important difference with `Process.WaitForExit*` is that it does not wait for STD OUT/ERR to signal EOF. It just waits for the process to exit.

This is very important because pipe signals EOF only when every copy of the writing end handle is closed. It includes also the handles derived from the child process by the grand children.

Such design allows for avoding some very hard to diagnose bugs like [#51277](https://github.com/dotnet/runtime/issues/51277) or [#24855](https://github.com/dotnet/runtime/issues/24855).

Last, but not least both `WaitForExit` and `WaitForExitAsync` kill the process on timeout/cancellation.
From my resarch it seems to be the expected behavior in most scenarios. It also helps to implement it for OSes that don't support waiting for process exit with timeout natively (Linux Kernels prior 5.2 like RHEL 8.0).

```csharp
namespace Microsoft.Win32.SafeHandles;

public class SafeChildProcessHandle : SafeHandle
{
    public int ProcessId { get; }

    public SafeChildProcessHandle(IntPtr existingHandle, bool ownsHandle);

    public static SafeChildProcessHandle Start(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error); // NEW (https://github.com/dotnet/runtime/issues/28838)

    public void Kill();
    public void Resume(); // NEW (https://github.com/dotnet/runtime/issues/94127)

    [UnsupportedOSPlatform("windows")]
    public void SendSignal(PosixSignal signal);  // NEW (https://github.com/dotnet/runtime/issues/59746)

    public int WaitForExit(TimeSpan? timeout = default);
    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}
```

#### The enablers

The following APIs are not process-specific, but are enablers for advanced scenarios.

New methods on `System.Console` to access standard input, output, and error handles.
They are useful for explicit inheritance when starting process with provided STD IN/OUT/ERR ([proposal](https://github.com/dotnet/runtime/issues/122803)):

```csharp
namespace System;

public static class Console
{
    public static SafeFileHandle OpenStandardInputHandle();
    public static SafeFileHandle OpenStandardOutputHandle();
    public static SafeFileHandle OpenStandardErrorHandle();
}

namespace System.IO;

public static class File
{
    public static SafeFileHandle OpenNullFileHandle();
    public static void CreateAnonymousPipe(out SafeFileHandle read, out SafeFileHandle write);
}
```

`OpenNullFileHandle()` ([proposal](https://github.com/dotnet/runtime/issues/122803)) opens a handle to the null device (`NUL` on Windows, `/dev/null` on Unix).

It enables discarding process output. Natively, without any performance overhead ([benchmarks](https://github.com/adamsitnik/ProcessPlayground/blob/main/Benchmarks/Discard.cs)):

| Method   | Mean     | Ratio        | Completed Work Items | Allocated | Alloc Ratio   |
|--------- |---------:|-------------:|---------------------:|----------:|--------------:|
| Old      | 185.5 ms |     baseline |               8.0000 | 112.08 KB |               |
| OldAsync | 182.3 ms | 1.02x faster |               6.0000 |  89.44 KB |   1.253x less |
| New      | 172.5 ms | 1.08x faster |                    - |      1 KB | 112.078x less |
| NewAsync | 170.9 ms | 1.09x faster |               2.0000 |    1.4 KB |  80.145x less |


Or providing empty input , which as of today is often implemented like this:

```csharp
process.StartInfo.RedirectStandardInput = true;
process.Start();

process.StandardInput.Close(); // Send EOF
```

`CreateAnonymousPipe()` ([proposal](https://github.com/dotnet/runtime/issues/122806)) creates an anonymous pipe for inter-process communication.
It's possible to create anonymous pipes today, but it's complex, heavy and enforces handle inheritance. This method is the opposite (important to avoid accidental handle inheritance).

#### Example: Piping between processes

This example demonstrates piping output from one process to another using anonymous pipes, providing empty input, redirecting to file and discarding error:

```csharp
File.CreateAnonymousPipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe);

using (readPipe)
using (writePipe)
{
    ProcessStartOptions producer = new("cmd")
    {
        Arguments = { "/c", "echo hello world & echo test line & echo another test" }
    };
    ProcessStartOptions consumer = new("findstr")
    {
        Arguments = { "test" }
    };

    // Start producer with output redirected to the write end of the pipe, no input and discarding error.
    using SafeChildProcessHandle producerHandle = SafeChildProcessHandle.Start(producer, input: null, output: writePipe, error: null);

    // Start consumer with input from the read end of the pipe, writing output to file and discarding error.
    using SafeFileHandle outputHandle = File.OpenHandle("output.txt", FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    using SafeProcessHandle consumerHandle = SafeProcessHandle.Start(consumer, readPipe, outputHandle, error: null);

    // Wait for both processes to complete
    await producerHandle.WaitForExitAsync();
    await consumerHandle.WaitForExitAsync();
}
```

### Mid-level APIs

I've not provided any yet, but one example that comes to my mind a `Process`-like type with `Stream`s for STD IN/OUT/ERR.

### High-level APIs

High-level convenience methods for common process execution scenarios:

```csharp
namespace System.TBA;

public static class ChildProcess
{
    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to current process. Waits for its completion, returns exit code.
    /// </summary>
    public static int Execute(ProcessStartOptions options, TimeSpan? timeout = default);
    public static Task<int> ExecuteAsync(ProcessStartOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR discarded. Waits for its completion, returns exit code.
    /// </summary>
    public static int Discard(ProcessStartOptions options, TimeSpan? timeout = default);
    public static Task<int> DiscardAsync(ProcessStartOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes the process with STD IN/OUT/ERR redirected to specified files. Waits for its completion, returns exit code.
    /// </summary>
    public static int RedirectToFiles(ProcessStartOptions options, string? inputFile, string? outputFile, string? errorFile, TimeSpan? timeout = default);
    public static Task<int> RedirectToFilesAsync(ProcessStartOptions options, string? inputFile, string? outputFile, string? errorFile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an instance of <see cref="ProcessOutputLines"/> to stream the output of the process.
    /// </summary>
    public static ProcessOutputLines ReadOutputLines(ProcessStartOptions options, TimeSpan? timeout = null, Encoding? encoding = null);
        
    /// <summary>
    /// Executes the process and returns the combined output (STD OUT and ERR) as bytes.
    /// </summary>
    public static CombinedOutput GetCombinedOutput(ProcessStartOptions options, SafeFileHandle? input = null, TimeSpan? timeout = null);
    public static Task<CombinedOutput> GetCombinedOutputAsync(ProcessStartOptions options, SafeFileHandle? input = null, CancellationToken cancellationToken = default);
}
```

Very often .NET users run into deadlocks because they forget to read STD OUT/ERR, or they read only one of them.
In order to force them to read both, I've designed two high-level APIs that avoid such issues completely.
The process is not started until the user starts enumerating the output.

```csharp
namespace System.TBA;

public readonly struct ProcessOutputLine
{
    public string Content { get; }
    public bool StandardError { get; }
}

public class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>, IEnumerable<ProcessOutputLine>
{
    public int ProcessId { get; }  // Available after enumeration starts
    public int ExitCode { get; }   // Available after enumeration completes

    public IAsyncEnumerator<ProcessOutputLine> GetAsyncEnumerator(CancellationToken cancellationToken = default);
    public IEnumerator<ProcessOutputLine> GetEnumerator();
}
```

The `CombinedOutput` struct provides access to the complete output of a process as a byte array, which can be converted to text using the `GetText` method. This is useful when you need to capture all output efficiently without line-by-line processing.


```csharp
namespace System.TBA;

public readonly struct CombinedOutput
{
    public int ExitCode { get; }
    public ReadOnlyMemory<byte> Bytes { get; }
    public int ProcessId { get; }

    public string GetText(Encoding? encoding = null);
}
```

#### Usage Examples