# ProcessPlayground

A playground for exploring new APIs for running command-line processes in .NET.

## Motivation

Running external processes and capturing their output is a common task in .NET applications, but the current `System.Diagnostics.Process` API has several pain points:

1. **Verbose and error-prone**: Configuring `ProcessStartInfo` correctly requires setting multiple properties
2. **Inefficient output capture**: Users often implement inefficient patterns to consume output (e.g., using event handlers that allocate per-line or reading to end then discarding)
3. **No native file redirection**: Redirecting output to files requires reading the output stream and writing to files manually, which is expensive
4. **Timeout and cancellation handling**: Users frequently need to implement their own Ctrl+C handling and timeout logic

The `CommandLineInfo` API addresses these issues with a simpler, more efficient design.

## Key Types

### `CommandLineInfo`

The main class for configuring and executing a command-line process. Similar to `ProcessStartInfo`, but with a simpler API focused on common use cases.

```csharp
CommandLineInfo info = new("dotnet")
{
    Arguments = { "--help" },
    WorkingDirectory = new DirectoryInfo("/path/to/dir"),
    Environment = { ["MY_VAR"] = "value" },
    CreateNoWindow = true,
    KillOnCancelKeyPress = true
};
```

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `FileName` | `string` | The name of the executable to run (required) |
| `Arguments` | `IList<string>` | Command-line arguments to pass to the process |
| `Environment` | `IDictionary<string, string?>` | Environment variables for the child process |
| `WorkingDirectory` | `DirectoryInfo?` | Working directory for the child process |
| `CreateNoWindow` | `bool` | Whether to create a console window |
| `KillOnCancelKeyPress` | `bool` | Whether to kill the process when Ctrl+C is pressed |

### `CommandLineOutput`

An async enumerable that streams output lines from a command-line process.

```csharp
var output = info.ReadOutputAsync();
await foreach (var line in output)
{
    Console.WriteLine(line.Content);
}
Console.WriteLine($"Process {output.ProcessId} exited with: {output.ExitCode}");
```

### `OutputLine`

A readonly struct representing a single line of output.

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
CommandLineInfo info = new("dotnet")
{
    Arguments = { "--help" }
};

int exitCode = info.Execute();
// or async
int exitCode = await info.ExecuteAsync();
```

### Execute with Timeout

```csharp
CommandLineInfo info = new("ping")
{
    Arguments = { "microsoft.com", "-t" },  // Ping until stopped
    KillOnCancelKeyPress = true
};

// Kill after 3 seconds
int exitCode = info.Execute(TimeSpan.FromSeconds(3));

// or with CancellationToken
using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
int exitCode = await info.ExecuteAsync(cts.Token);
```

### Discard Output

When you need to run a process but don't care about its output:

```csharp
CommandLineInfo info = new("dotnet")
{
    Arguments = { "--help" }
};

int exitCode = info.Discard();
// or async
int exitCode = await info.DiscardAsync();
```

This is more efficient than the traditional approach of redirecting output and discarding it in event handlers:

```csharp
// Old approach (inefficient)
process.StartInfo.RedirectStandardOutput = true;
process.StartInfo.RedirectStandardError = true;
process.OutputDataReceived += (sender, e) => { };  // Allocates per-line
process.ErrorDataReceived += (sender, e) => { };
```

### Redirect to Files

Redirect stdin/stdout/stderr directly to files without reading through .NET:

```csharp
CommandLineInfo info = new("dotnet")
{
    Arguments = { "--help" }
};

int exitCode = info.RedirectToFiles(
    inputFile: null,           // null = NUL device (EOF)
    outputFile: "output.txt",  // stdout goes here
    errorFile: "error.txt"     // stderr goes here, or use same as outputFile
);

// or async
int exitCode = await info.RedirectToFilesAsync(null, "output.txt", null);
```

This is significantly faster than reading output through pipes and writing to files manually.

### Stream Output Lines

For streaming output line-by-line as an async enumerable:

```csharp
CommandLineInfo info = new("dotnet")
{
    Arguments = { "--help" }
};

var output = info.ReadOutputAsync();
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

### Stream with Timeout and Cancellation

```csharp
CommandLineInfo info = new("ping")
{
    Arguments = { "microsoft.com", "-t" },
    KillOnCancelKeyPress = true
};

using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
await foreach (var line in info.ReadOutputAsync().WithCancellation(cts.Token))
{
    Console.WriteLine(line.Content);
}
```

## Comparison with Process API

| Task | Process API | CommandLineInfo API |
|------|-------------|---------------------|
| Run and wait | `Process.Start()` + `WaitForExit()` | `info.Execute()` |
| Run async | `Process.Start()` + `WaitForExitAsync()` | `info.ExecuteAsync()` |
| Discard output | Redirect + empty event handlers | `info.Discard()` |
| Redirect to file | Redirect + read + write to file | `info.RedirectToFiles()` |
| Stream output | Redirect + `ReadLineAsync` loop | `info.ReadOutputAsync()` |
| Ctrl+C handling | Manual `CancelKeyPress` subscription | `KillOnCancelKeyPress = true` |
| Timeout | Manual timer + kill | `Execute(TimeSpan)` or `CancellationToken` |

## Project Structure

- **Library/**: Core implementation of `CommandLineInfo` and related types
- **ConsoleApp/**: Sample console application demonstrating usage
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

## Running Benchmarks

```bash
cd Benchmarks
dotnet run -c Release
```

## License

MIT License - see [LICENSE](LICENSE) for details.
