# ProcessPlayground

A playground for exploring new APIs for running command-line processes in .NET.

## Motivation

Running external processes and capturing their output is a common task in .NET applications, but the current `System.Diagnostics.Process` API has several pain points:

1. **Verbose and error-prone**: Configuring `ProcessStartInfo` correctly requires setting multiple properties
2. **Inefficient output capture**: Users often implement inefficient patterns to consume output (e.g., using event handlers that allocate per-line or reading to end then discarding)
3. **No native file redirection**: Redirecting output to files requires reading the output stream and writing to files manually, which is expensive
4. **Timeout and cancellation handling**: Users frequently need to implement their own Ctrl+C handling and timeout logic

This playground explores a new design that addresses these issues with a layered API approach: low-level primitives for advanced scenarios, and high-level convenience methods for common use cases.

## API Overview

### Low-Level APIs: Console Handle Access

New methods on `System.Console` to access standard input, output, and error handles:

```csharp
namespace System;

public static class Console
{
    public static SafeFileHandle OpenStandardInputHandle();
    public static SafeFileHandle OpenStandardOutputHandle();
    public static SafeFileHandle OpenStandardErrorHandle();
}
```

These APIs provide direct access to the standard handles of the current process, enabling advanced scenarios like handle inheritance and redirection.

### Low-Level APIs: File Utilities

New methods on `System.IO.File` for process I/O scenarios:

```csharp
namespace System.IO;
    
public static class File
{
    public static SafeFileHandle OpenNullFileHandle();
    public static void CreateAnonymousPipe(out SafeFileHandle read, out SafeFileHandle write);
    public static void CreateNamedPipe(out SafeFileHandle read, out SafeFileHandle write, string? name = null);
}
```

- **`OpenNullFileHandle()`**: Opens a handle to the null device (`NUL` on Windows, `/dev/null` on Unix). Useful for discarding process output or providing empty input.
- **`CreateAnonymousPipe()`**: Creates an anonymous pipe for inter-process communication. The read end can be used to read data written to the write end.
- **`CreateNamedPipe()`**: Creates a named pipe for inter-process communication. The read end is async and the write end is sync. Primarily used internally for timeout scenarios on Windows.

### ProcessStartOptions

An option bag class for configuring process creation. Similar to `ProcessStartInfo`, but simpler:

```csharp
namespace System.TBA;

public sealed class ProcessStartOptions
{
    public string FileName { get; }
    public IList<string> Arguments { get; set; }
    public IDictionary<string, string?> Environment { get; }
    public IList<SafeHandle> InheritedHandles { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool CreateNoWindow { get; set; }
    public bool KillOnParentExit { get; set; }
    public bool CreateNewProcessGroup { get; set; }

    public ProcessStartOptions(string fileName);
    
    public static ProcessStartOptions ResolvePath(string fileName);
}
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `FileName` | `string` | The name of the executable to run (required) |
| `Arguments` | `IList<string>` | Command-line arguments to pass to the process (settable) |
| `Environment` | `IDictionary<string, string?>` | Environment variables for the child process |
| `InheritedHandles` | `IList<SafeHandle>` | Handles to inherit in the child process (settable) |
| `WorkingDirectory` | `string?` | Working directory for the child process |
| `CreateNoWindow` | `bool` | Whether to create a console window |
| `KillOnParentExit` | `bool` | Whether to kill the process when the parent process exits |
| `CreateNewProcessGroup` | `bool` | Whether to create the process in a new process group |

**Static Methods:**

| Method | Description |
|--------|-------------|
| `ResolvePath(string fileName)` | Resolves the given file name to an absolute path by searching the current directory, executable directory, system directories (Windows), and PATH environment variable. Returns a new ProcessStartOptions instance with the resolved path. Throws `FileNotFoundException` if the file cannot be found. |

### Low-Level APIs: SafeChildProcessHandle

Low-level APIs for advanced process management scenarios:

```csharp
namespace Microsoft.Win32.SafeHandles;

public class SafeChildProcessHandle
{
    public static SafeChildProcessHandle Start(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error);
    public static SafeChildProcessHandle StartSuspended(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error);
    public static SafeChildProcessHandle Open(int processId);
    
    public int ProcessId { get; }
    
    public ProcessExitStatus WaitForExit();
    public bool TryWaitForExit(TimeSpan timeout, out ProcessExitStatus? exitStatus);
    public ProcessExitStatus WaitForExitOrKillOnTimeout(TimeSpan timeout);
    public Task<ProcessExitStatus> WaitForExitAsync(CancellationToken cancellationToken = default);
    public Task<ProcessExitStatus> WaitForExitOrKillOnCancellationAsync(CancellationToken cancellationToken);
    
    public bool Kill();
    public bool KillProcessGroup();
    public void Resume();
    public void Signal(PosixSignal signal);  // Unix-specific signals, limited Windows support
    public void SignalProcessGroup(PosixSignal signal);  // Unix only
}
```

The new `SafeChildProcessHandle` APIs provide fine-grained control over process creation and lifecycle management. They enable advanced scenarios like piping between processes.

**Example: Piping between processes**

This example demonstrates piping output from one process to another using anonymous pipes:

```csharp
using System.TBA;  // The actual namespace for these APIs
using Microsoft.Win32.SafeHandles;

// Create an anonymous pipe
File.CreateAnonymousPipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe);

using (readPipe)
using (writePipe)
{
    // Producer process writes to the pipe
    ProcessStartOptions producer = new("cmd")
    {
        Arguments = { "/c", "echo hello world & echo test line & echo another test" }
    };
    
    // Consumer process reads from the pipe
    ProcessStartOptions consumer = new("findstr")
    {
        Arguments = { "test" }
    };

    // Start producer with output redirected to the write end of the pipe
    using SafeChildProcessHandle producerHandle = SafeChildProcessHandle.Start(producer, input: null, output: writePipe, error: null);

    // Start consumer with input from the read end of the pipe
    using SafeFileHandle outputHandle = File.OpenHandle("output.txt", FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    using SafeChildProcessHandle consumerHandle = SafeChildProcessHandle.Start(consumer, readPipe, outputHandle, error: null);

    // Wait for both processes to complete
    await producerHandle.WaitForExitAsync();
    await consumerHandle.WaitForExitAsync();
}

// Read the filtered output
string result = await File.ReadAllTextAsync("output.txt");
Console.WriteLine(result); // Prints "test line" and "another test"
```

### High-Level APIs: ChildProcess

High-level convenience methods for common process execution scenarios:

```csharp
namespace System.TBA
{
    public static class ChildProcess
    {
        /// <summary>
        /// Executes the process with STD IN/OUT/ERR redirected to current process. Waits for its completion, returns exit code.
        /// </summary>
        public static int Inherit(ProcessStartOptions options, TimeSpan? timeout = default);
        public static Task<int> InheritAsync(ProcessStartOptions options, CancellationToken cancellationToken = default);

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
        public static ProcessOutputLines StreamOutputLines(ProcessStartOptions options, TimeSpan? timeout = null, Encoding? encoding = null);
        
        /// <summary>
        /// Executes the process and returns the standard output and error as strings.
        /// </summary>
        public static ProcessOutput CaptureOutput(ProcessStartOptions options, Encoding? encoding = null, SafeFileHandle? input = null, TimeSpan? timeout = null);
        public static Task<ProcessOutput> CaptureOutputAsync(ProcessStartOptions options, Encoding? encoding = null, SafeFileHandle? input = null, CancellationToken cancellationToken = default);
        
        /// <summary>
        /// Executes the process and returns the combined output (stdout + stderr) as bytes.
        /// </summary>
        public static CombinedOutput CaptureCombined(ProcessStartOptions options, SafeFileHandle? input = null, TimeSpan? timeout = null);
        public static Task<CombinedOutput> CaptureCombinedAsync(ProcessStartOptions options, SafeFileHandle? input = null, CancellationToken cancellationToken = default);
    }
}
```

### ProcessOutputLines

An async enumerable that streams output lines from a command-line process:

```csharp
namespace System.TBA;

public class ProcessOutputLines : IAsyncEnumerable<ProcessOutputLine>, IEnumerable<ProcessOutputLine>
{
    public int ProcessId { get; }  // Available after enumeration starts
    public int ExitCode { get; }   // Available after enumeration completes
}
```

The `ProcessOutputLines` class allows you to read output lines as they are produced by the process, avoiding deadlocks and excessive memory usage.

### ProcessOutputLine

A readonly struct representing a single line of output:

```csharp
namespace System.TBA;

public readonly struct ProcessOutputLine
{
    public string Content { get; }      // The text content of the line
    public bool StandardError { get; }  // True if from stderr, false if from stdout
}
```

### ProcessOutput

A readonly struct representing the captured output from a process:

```csharp
namespace System.TBA;

public readonly struct ProcessOutput
{
    public ProcessExitStatus ExitStatus { get; }  // The exit status of the process
    public string StandardOutput { get; }  // The decoded string content from stdout
    public string StandardError { get; }   // The decoded string content from stderr
    public int ProcessId { get; }          // The process ID
}
```

The `ProcessOutput` struct provides access to the complete output of a process as separate stdout and stderr strings. This is useful when you need to capture all output and distinguish between standard output and standard error.

### CombinedOutput

A readonly struct representing the complete output from a process:

```csharp
namespace System.TBA;

public readonly struct CombinedOutput
{
    public int ExitCode { get; }           // The exit code of the process
    public ReadOnlyMemory<byte> Bytes { get; }  // The combined stdout and stderr as bytes
    public int ProcessId { get; }          // The process ID
    
    public string GetText(Encoding? encoding = null);  // Convert bytes to string
}
```

The `CombinedOutput` struct provides access to the complete output of a process as a byte array, which can be converted to text using the `GetText` method. This is useful when you need to capture all output efficiently without line-by-line processing.

## Usage Examples

### Execute a Process

The simplest way to run a process with stdin/stdout/stderr redirected to the current process:

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--help" }
};

ProcessExitStatus exitStatus = ChildProcess.Inherit(options);
// or async
ProcessExitStatus exitStatus = await ChildProcess.InheritAsync(options);
```

### Execute with Timeout

```csharp
ProcessStartOptions options = new("ping")
{
    Arguments = { "microsoft.com", "-t" }  // Ping until stopped
};

// Kill after 3 seconds
ProcessExitStatus exitStatus = ChildProcess.Inherit(options, TimeSpan.FromSeconds(3));

// or with CancellationToken
using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
ProcessExitStatus exitStatus = await ChildProcess.InheritAsync(options, cts.Token);
```

### Discard Output

When you need to run a process but don't care about its output:

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--help" }
};

ProcessExitStatus exitStatus = ChildProcess.Discard(options);
// or async
ProcessExitStatus exitStatus = await ChildProcess.DiscardAsync(options);
```

This is more efficient than the traditional approach of redirecting output and discarding it in event handlers:

```csharp
process.StartInfo.RedirectStandardOutput = true;
process.StartInfo.RedirectStandardError = true;

process.OutputDataReceived += (sender, e) => { };  // Allocates per-line
process.ErrorDataReceived += (sender, e) => { };

process.Start();

process.BeginOutputReadLine();
process.BeginErrorReadLine();

process.WaitForExit();
```

### Redirect to Files

Redirect stdin/stdout/stderr directly to files without reading through .NET:

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--help" }
};

ProcessExitStatus exitStatus = ChildProcess.RedirectToFiles(
    options,
    inputFile: null,           // null = NUL device (EOF)
    outputFile: "output.txt",  // stdout goes here
    errorFile: "error.txt"     // stderr goes here, or use same as outputFile
);

// or async
ProcessExitStatus exitStatus = await ChildProcess.RedirectToFilesAsync(options, null, "output.txt", null);
```

This is significantly faster than reading output through pipes and writing to files manually.

### Stream Output Lines

For streaming output line-by-line as an async enumerable to avoid any deadlocks (the design forces the user to consume the output):

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--help" }
};

var output = ChildProcess.StreamOutputLines(options);
await foreach (var line in output)
{
    if (line.StandardError)
    {
        Console.Error.WriteLine($"ERR: {line.Content}");
    }
    else
    {
        Console.WriteLine($"OUT: {line.Content}");
    }
}
Console.WriteLine($"Process {output.ProcessId} exited with: {output.ExitCode}");
```

### Capture Output

For capturing process output as separate stdout and stderr strings:

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--version" }
};

// Synchronous version
ProcessOutput output = ChildProcess.CaptureOutput(options);
Console.WriteLine($"Standard Output: {output.StandardOutput}");
Console.WriteLine($"Standard Error: {output.StandardError}");
Console.WriteLine($"Exit code: {output.ExitStatus.ExitCode}");

// Async version
ProcessOutput output = await ChildProcess.CaptureOutputAsync(options);
Console.WriteLine($"Standard Output: {output.StandardOutput}");
Console.WriteLine($"Standard Error: {output.StandardError}");
Console.WriteLine($"Exit code: {output.ExitStatus.ExitCode}");
```

### Get Combined Output

For efficiently capturing all process output as bytes or text:

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--version" }
};

// Synchronous version
CombinedOutput output = ChildProcess.CaptureCombined(options);
string text = output.GetText();
Console.WriteLine($"Output: {text}");
Console.WriteLine($"Exit code: {output.ExitCode}");

// Async version
CombinedOutput output = await ChildProcess.CaptureCombinedAsync(options);
string text = output.GetText();
Console.WriteLine($"Output: {text}");
```

## Comparison with Process API

| Task | Process API | New API |
|------|-------------|---------|
| Run and wait | `Process.Start()` + `WaitForExit()` | `ChildProcess.Inherit()` |
| Run async | `Process.Start()` + `WaitForExitAsync()` | `ChildProcess.InheritAsync()` |
| Discard output | Redirect + empty event handlers | `ChildProcess.Discard()` |
| Redirect to file | Redirect + read + write to file | `ChildProcess.RedirectToFiles()` |
| Stream output | Redirect + `ReadLineAsync` loop | `ChildProcess.StreamOutputLines()` |
| Capture stdout/stderr as separate strings | Redirect + `ReadToEndAsync()` | `ChildProcess.CaptureOutput()` |
| Capture combined stdout/stderr as bytes | Redirect + `ReadToEndAsync()` | `ChildProcess.CaptureCombined()` |
| Piping between processes | Complex handle management | `SafeChildProcessHandle.Start()` with pipes |
| Parent exit handling | Manual implementation | `KillOnParentExit = true` |
| Timeout | `WaitForExit(int)` + `Kill` | `Inherit(TimeSpan)` or `CancellationToken` |
| Path resolution | Manual PATH search | `ProcessStartOptions.ResolvePath()` |

## Code Examples: OLD vs NEW API

This section shows side-by-side comparisons of common process execution scenarios using the old `Process` API versus the new `ChildProcess` API.

### Scenario 1: Discard Output

When you need to run a process but don't care about its output:

<table>
<tbody><tr>
<th>OLD API</th>
<th>NEW API</th>
</tr>
<tr>
<td>

```csharp
using (Process process = new())
{
    process.StartInfo.FileName = "dotnet";
    process.StartInfo.Arguments = "--help";
    process.StartInfo.RedirectStandardOutput = true;
    process.StartInfo.RedirectStandardError = true;

    process.OutputDataReceived += (sender, e) => { };
    process.ErrorDataReceived += (sender, e) => { };

    process.Start();

    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    process.WaitForExit();

    return process.ExitCode;
}
```
</td>
<td>

```csharp
ProcessStartOptions info = new("dotnet")
{
    Arguments = { "--help" },
};

return ChildProcess.Discard(info);
```
</td>
</tr></tbody></table>

### Scenario 2: Inherit Standard Streams

When you want the child process to use the parent's stdin/stdout/stderr:

<table>
<tbody><tr>
<th>OLD API</th>
<th>NEW API</th>
</tr>
<tr>
<td>

```csharp
ProcessStartInfo info = new()
{
    FileName = "dotnet",
    Arguments = "--help",
    UseShellExecute = false
};

using Process process = Process.Start(info)!;
process.WaitForExit();
```
</td>
<td>

```csharp
ProcessStartOptions info = new("dotnet")
{
    Arguments = { "--help" },
};

return ChildProcess.Inherit(info);
```
</td>
</tr></tbody></table>

### Scenario 3: Capture Output

When you need to capture stdout and stderr as separate strings:

<table>
<tbody><tr>
<th>OLD API</th>
<th>NEW API</th>
</tr>
<tr>
<td>

```csharp
ProcessStartInfo info = new()
{
    FileName = "dotnet",
    Arguments = "--help",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};

using Process process = Process.Start(info)!;

Task<string> readOutput = process.StandardOutput.ReadToEndAsync();
Task<string> readError = process.StandardError.ReadToEndAsync();

string output = await readOutput;
string error = await readError;
await process.WaitForExitAsync();

int exitCode = process.ExitCode;
```
</td>
<td>

```csharp
ProcessStartOptions info = new("dotnet")
{
    Arguments = { "--help" },
};

ProcessOutput result = await ChildProcess.CaptureOutputAsync(info);

string output = result.StandardOutput;
string error = result.StandardError;
int exitCode = result.ExitCode;
```
</td>
</tr></tbody></table>

### Scenario 4: Redirect to File

When you need to redirect stdout to a file:

<table>
<tbody><tr>
<th>OLD API</th>
<th>NEW API</th>
</tr>
<tr>
<td>

```csharp
using TextWriter text = new StreamWriter("output.txt");
using (Process process = new())
{
    process.StartInfo.FileName = "dotnet";
    process.StartInfo.Arguments = "--help";
    process.StartInfo.RedirectStandardOutput = true;

    process.OutputDataReceived += (sender, e) => text.WriteLine(e.Data);

    process.Start();

    process.BeginOutputReadLine();

    process.WaitForExit();
}
```
</td>
<td>

```csharp
ProcessStartOptions info = new("dotnet")
{
    Arguments = { "--help" },
};

ChildProcess.RedirectToFiles(info, 
    inputFile: null, 
    outputFile: "output.txt", 
    errorFile: null);
```
</td>
</tr></tbody></table>

### Scenario 5: Stream Output Lines

When you need to process output line-by-line as it arrives:

<table>
<tbody><tr>
<th>OLD API</th>
<th>NEW API</th>
</tr>
<tr>
<td>

```csharp
ProcessStartInfo info = new()
{
    FileName = "dotnet",
    Arguments = "--help",
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false
};

using Process process = Process.Start(info)!;

Task<string?> readOutput = process.StandardOutput.ReadLineAsync();
Task<string?> readError = process.StandardError.ReadLineAsync();

while (true)
{
    Task completedTask = await Task.WhenAny(readOutput, readError);
    
    bool isError = completedTask == readError;
    string? line = await(isError ? readError : readOutput);
    if (line is null)
    {
        // Reached end of stream, consume the other stream fully
        line = await (isError ? readOutput : readError);
        while (line is not null)
        {
            line = await (isError ? readOutput : readError);
        }
        break;
    }
    
    // Process the line here (placeholder)
    _ = line;
    
    if (isError)
        readError = process.StandardError.ReadLineAsync();
    else
        readOutput = process.StandardOutput.ReadLineAsync();
}

return process.ExitCode;
```
</td>
<td>

```csharp
ProcessStartOptions info = new("dotnet")
{
    Arguments = { "--help" },
};

var lines = ChildProcess.StreamOutputLines(info);
await foreach (var line in lines)
{
    // Process the line here (placeholder)
    _ = line.Content;
}

return lines.ExitCode;
```
</td>
</tr></tbody></table>

## Project Structure

- **Library/**: Core implementation of the process APIs
  - Low-level handle APIs (`Console`, `File`)
  - `ProcessStartOptions` configuration class
  - `SafeChildProcessHandle` for advanced process control
  - `ChildProcess` high-level convenience methods
  - `ProcessOutputLines` for streaming process output lines
- **ConsoleApp/**: Sample console application demonstrating usage
- **Tests/**: Unit tests including piping examples
- **Benchmarks/**: BenchmarkDotNet benchmarks comparing performance (C#)
- **BenchmarksGo/**: Go benchmarks for process execution patterns

## Building

```bash
dotnet build
```

## Running Samples

```bash
cd ConsoleApp
dotnet run
```

## Running Tests

```bash
cd Tests
dotnet test
```

## Running Benchmarks

### C# Benchmarks

```bash
cd Benchmarks
dotnet run -c Release --filter *
```

### Go Benchmarks

```bash
cd BenchmarksGo
go test -bench=. -benchmem
```

See [BenchmarksGo/README.md](BenchmarksGo/README.md) for detailed instructions on running Go benchmarks.

## License

MIT License - see [LICENSE](LICENSE) for details.
