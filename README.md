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
namespace System
{
    public static class Console
    {
        public static SafeFileHandle GetStdInputHandle();
        public static SafeFileHandle GetStdOutputHandle();
        public static SafeFileHandle GetStdErrorHandle();
    }
}
```

These APIs provide direct access to the standard handles of the current process, enabling advanced scenarios like handle inheritance and redirection.

### Low-Level APIs: File Utilities

New methods on `System.IO.File` for process I/O scenarios:

```csharp
namespace System.IO
{
    public static class File
    {
        public static SafeFileHandle OpenNullFileHandle();
        public static void CreateAnonymousPipe(out SafeFileHandle read, out SafeFileHandle write);
    }
}
```

- **`OpenNullFileHandle()`**: Opens a handle to the null device (`NUL` on Windows, `/dev/null` on Unix). Useful for discarding process output or providing empty input.
- **`CreateAnonymousPipe()`**: Creates an anonymous pipe for inter-process communication. The read end can be used to read data written to the write end.

### ProcessStartOptions

An option bag class for configuring process creation. Similar to `ProcessStartInfo`, but simpler:

```csharp
namespace System.TBA
{
    public sealed class ProcessStartOptions
    {
        public string FileName { get; }
        public IList<string> Arguments { get; }
        public IDictionary<string, string?> Environment { get; }
        public DirectoryInfo? WorkingDirectory { get; set; }
        public bool CreateNoWindow { get; set; }
        public bool KillOnParentDeath { get; set; }

        public ProcessStartOptions(string fileName);
    }
}
```

**Properties:**

| Property | Type | Description |
|----------|------|-------------|
| `FileName` | `string` | The name of the executable to run (required) |
| `Arguments` | `IList<string>` | Command-line arguments to pass to the process |
| `Environment` | `IDictionary<string, string?>` | Environment variables for the child process |
| `WorkingDirectory` | `DirectoryInfo?` | Working directory for the child process |
| `CreateNoWindow` | `bool` | Whether to create a console window |
| `KillOnParentDeath` | `bool` | Whether to kill the process when the parent process exits |

### Low-Level APIs: ProcessHandle

Low-level APIs for advanced process management scenarios:

```csharp
namespace System.TBA
{
    public static class ProcessHandle
    {
        public static SafeProcessHandle Start(ProcessStartOptions options, SafeFileHandle? input, SafeFileHandle? output, SafeFileHandle? error);
        public static int GetProcessId(SafeProcessHandle processHandle);
        public static int WaitForExit(SafeProcessHandle processHandle, TimeSpan? timeout = default);
        public static Task<int> WaitForExitAsync(SafeProcessHandle processHandle, CancellationToken cancellationToken = default);
    }
}
```

The `ProcessHandle` APIs provide fine-grained control over process creation and lifecycle management. They enable advanced scenarios like piping between processes.

**Example: Piping between processes**

This example demonstrates piping output from one process to another using anonymous pipes:

```csharp
using Library;
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
    using SafeProcessHandle producerHandle = ProcessHandle.Start(producer, input: null, output: writePipe, error: null);

    // Close write end in parent so consumer will get EOF
    writePipe.Close();

    // Start consumer with input from the read end of the pipe
    using SafeFileHandle outputHandle = File.OpenHandle("output.txt", FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    using SafeProcessHandle consumerHandle = ProcessHandle.Start(consumer, readPipe, outputHandle, error: null);

    // Wait for both processes to complete
    await ProcessHandle.WaitForExitAsync(producerHandle);
    await ProcessHandle.WaitForExitAsync(consumerHandle);
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
        /// Creates an instance of <see cref="CommandLineOutput"/> to stream the output of the process.
        /// </summary>
        public static CommandLineOutput ReadOutputAsync(ProcessStartOptions options, Encoding? encoding = null);
    }
}
```

### CommandLineOutput

An async enumerable that streams output lines from a command-line process:

```csharp
public class CommandLineOutput : IAsyncEnumerable<OutputLine>
{
    public int ProcessId { get; }  // Available after enumeration starts
    public int ExitCode { get; }   // Available after enumeration completes
}
```

### OutputLine

A readonly struct representing a single line of output:

```csharp
public readonly struct OutputLine
{
    public string Content { get; }      // The text content of the line
    public bool StandardError { get; }  // True if from stderr, false if from stdout
}
```

## Usage Examples

### Execute a Process

The simplest way to run a process with stdin/stdout/stderr redirected to the current process:

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--help" }
};

int exitCode = ChildProcess.Execute(options);
// or async
int exitCode = await ChildProcess.ExecuteAsync(options);
```

### Execute with Timeout

```csharp
ProcessStartOptions options = new("ping")
{
    Arguments = { "microsoft.com", "-t" }  // Ping until stopped
};

// Kill after 3 seconds
int exitCode = ChildProcess.Execute(options, TimeSpan.FromSeconds(3));

// or with CancellationToken
using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
int exitCode = await ChildProcess.ExecuteAsync(options, cts.Token);
```

### Discard Output

When you need to run a process but don't care about its output:

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--help" }
};

int exitCode = ChildProcess.Discard(options);
// or async
int exitCode = await ChildProcess.DiscardAsync(options);
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

int exitCode = ChildProcess.RedirectToFiles(
    options,
    inputFile: null,           // null = NUL device (EOF)
    outputFile: "output.txt",  // stdout goes here
    errorFile: "error.txt"     // stderr goes here, or use same as outputFile
);

// or async
int exitCode = await ChildProcess.RedirectToFilesAsync(options, null, "output.txt", null);
```

This is significantly faster than reading output through pipes and writing to files manually.

### Stream Output Lines

For streaming output line-by-line as an async enumerable to avoid any deadlocks:

```csharp
ProcessStartOptions options = new("dotnet")
{
    Arguments = { "--help" }
};

var output = ChildProcess.ReadOutputAsync(options);
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

## Comparison with Process API

| Task | Process API | New API |
|------|-------------|---------|
| Run and wait | `Process.Start()` + `WaitForExit()` | `ChildProcess.Execute()` |
| Run async | `Process.Start()` + `WaitForExitAsync()` | `ChildProcess.ExecuteAsync()` |
| Discard output | Redirect + empty event handlers | `ChildProcess.Discard()` |
| Redirect to file | Redirect + read + write to file | `ChildProcess.RedirectToFiles()` |
| Stream output | Redirect + `ReadLineAsync` loop | `ChildProcess.ReadOutputAsync()` |
| Piping between processes | Complex handle management | `ProcessHandle.Start()` with pipes |
| Parent death handling | Manual implementation | `KillOnParentDeath = true` |
| Timeout | `WaitForExit(int)` + `Kill` | `Execute(TimeSpan)` or `CancellationToken` |

## Project Structure

- **Library/**: Core implementation of the process APIs
  - Low-level handle APIs (`Console`, `File`)
  - `ProcessStartOptions` configuration class
  - `ProcessHandle` for advanced process control
  - `ChildProcess` high-level convenience methods
  - `CommandLineOutput` for streaming process output
- **ConsoleApp/**: Sample console application demonstrating usage
- **Tests/**: Unit tests including piping examples
- **Benchmarks/**: BenchmarkDotNet benchmarks comparing performance

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

```bash
cd Benchmarks
dotnet run -c Release --filter *
```

## License

MIT License - see [LICENSE](LICENSE) for details.
