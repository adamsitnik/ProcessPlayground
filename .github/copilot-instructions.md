# GitHub Copilot Instructions for ProcessPlayground

This document contains coding guidelines and conventions specific to the ProcessPlayground repository. Please follow these instructions when generating code or reviewing pull requests.

## Coding Style and Conventions

### Type Declarations

**Don't use the `var` keyword.** Always use explicit type names with target-typed `new()` syntax:

```csharp
// ❌ Incorrect
var options = new ProcessStartOptions("cmd");
var exitStatus = processHandle.WaitForExit();

// ✅ Correct
ProcessStartOptions options = new("cmd");
ExitStatus exitStatus = processHandle.WaitForExit();
```

Exception: You may use `var` in `foreach` loops and when the type is obvious from the right-hand side (e.g., `var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(...)`).

### Test Methods

**All test methods must be static.** Every method marked with `[Fact]` or `[Theory]` must include the `static` modifier:

```csharp
// ❌ Incorrect
[Fact]
public void MyTest()
{
    // ...
}

// ✅ Correct
[Fact]
public static void MyTest()
{
    // ...
}
```

### Assertions in Tests

**Don't use `Assert.Contains`.** Always use `Assert.Equal` to perform 100% explicit content checks:

```csharp
// ❌ Incorrect
Assert.Contains("Hello", output);
Assert.Contains("test", result.StandardOutput);

// ✅ Correct
Assert.Equal("Hello from stdout", output);
Assert.Equal("test", result.StandardOutput.Trim());
```

This ensures tests are explicit about the expected output and catch unexpected variations.

### Comments

**Don't add comments for obvious things.** Only add comments when they provide meaningful context that isn't clear from the code itself:

```csharp
// ❌ Incorrect - obvious comment
// Create a process handle
using SafeChildProcessHandle handle = SafeChildProcessHandle.Start(options, null, null, null);

// ❌ Incorrect - obvious comment
// Kill the process
handle.Kill();

// ✅ Correct - meaningful comment explaining why
// Use PowerShell because timeout.exe requires valid STD IN
ProcessStartOptions options = new("powershell") { Arguments = { "-Command", "Start-Sleep 5" } };
```

### XML Documentation

**Use proper XML docs syntax to reference types and members.** Don't use raw strings to reference code elements:

```csharp
// ❌ Incorrect
/// <param name="entireProcessGroup">When true, terminates the entire process group (requires CreateNewProcessGroup). Default is false.</param>

// ✅ Correct
/// <param name="entireProcessGroup">When true, terminates the entire process group. Requires <see cref="ProcessStartOptions.CreateNewProcessGroup"/>. Default is false.</param>
```

### Argument Validation

**Use built-in validation methods** instead of manual if-throw patterns:

```csharp
// ❌ Incorrect
if (processId <= 0)
{
    throw new ArgumentOutOfRangeException(nameof(processId));
}

// ✅ Correct
ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(processId, 0);
```

## Native Code Conventions

### Avoid Unsafe Code

**Use `out` parameters instead of pointers** to avoid the `unsafe` keyword when possible:

```csharp
// ❌ Incorrect
[DllImport("Library")]
private static unsafe partial int open_process(int pid, int* out_pidfd);

// ✅ Correct
[DllImport("Library")]
private static partial int open_process(int pid, out int out_pidfd);
```

## Testing Guidelines

### Test Coverage

**Each PR that introduces new public API must provide tests for that API.** Ensure comprehensive test coverage for:
- Normal operation
- Edge cases
- Error conditions
- Platform-specific behavior (when applicable)

### Test Execution and Cleanup

**Place resource acquisition before try/finally blocks.** This ensures you don't attempt cleanup if resource creation fails:

```csharp
// ❌ Incorrect
try
{
    using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);
    Assert.False(handle.IsInvalid);
}
finally
{
    // Cleanup that might fail if Open() threw
}

// ✅ Correct
using SafeChildProcessHandle handle = SafeChildProcessHandle.Open(processId);
try
{
    Assert.False(handle.IsInvalid);
}
finally
{
    handle.Kill();
}
```

### Exception Testing

**Don't validate exception messages.** Exception messages may change and are not part of the contract:

```csharp
// ❌ Incorrect
PlatformNotSupportedException ex = Assert.Throws<PlatformNotSupportedException>(() => handle.SendSignal(PosixSignal.SIGTERM));
Assert.Equal("SendSignal is only supported on Unix.", ex.Message);

// ✅ Correct
Assert.Throws<PlatformNotSupportedException>(() => handle.SendSignal(PosixSignal.SIGTERM));
```

### Platform-Specific Testing

#### Windows Testing

**For Windows tests:**
- Use PowerShell `Start-Sleep` for delays instead of `timeout.exe`:
  ```csharp
  // ✅ Correct
  ProcessStartOptions options = new("powershell") 
  { 
      Arguments = { "-Command", "Start-Sleep 5" } 
  };
  ```
  
- If you must use `timeout.exe`, provide `Console.OpenStandardInputHandle()` as input:
  ```csharp
  // ✅ Correct
  using SafeFileHandle input = Console.OpenStandardInputHandle();
  ProcessStartOptions options = new("timeout") 
  { 
      Arguments = { "/t", "5", "/nobreak" } 
  };
  using SafeChildProcessHandle handle = SafeChildProcessHandle.Start(options, input, null, null);
  ```

#### Cross-Platform Testing

**Use helper methods for output capture** instead of manual redirection:

```csharp
// ❌ Incorrect - manual output handling
ProcessStartOptions options = new("echo") { Arguments = { "test" } };
// ... complex output redirection ...

// ✅ Correct - use helper
ProcessStartOptions options = new("echo") { Arguments = { "test" } };
ProcessOutput output = ChildProcess.CaptureOutput(options);
Assert.Equal("test", output.StandardOutput.Trim());
```

### Test Timing

**Use shorter wait times in tests:**
- Maximum wait times: 1-5 seconds for process completion
- `Thread.Sleep`: 100ms for synchronization delays
- Avoid long timeouts that slow down test execution

```csharp
// ❌ Incorrect
Thread.Sleep(2000);
ExitStatus exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(30));

// ✅ Correct
Thread.Sleep(100);
ExitStatus exitStatus = processHandle.WaitForExitOrKillOnTimeout(TimeSpan.FromSeconds(1));
```

### Test Output Validation

**Perform explicit equality checks on captured content:**

```csharp
// ❌ Incorrect - just checking for completion
ProcessOutput output = ChildProcess.CaptureOutput(options);
Assert.Equal(0, output.ExitCode);

// ✅ Correct - explicit content check
ProcessOutput output = ChildProcess.CaptureOutput(options);
Assert.Equal("test", output.StandardOutput.Trim());
Assert.Equal(0, output.ExitCode);
```

## Consistency

**Be consistent with existing code patterns.** When adding new code:
- Match the formatting style of the file you're editing
- Follow the patterns used in similar existing tests or methods
- Use the same helper methods and utilities that existing code uses
- Maintain the same level of abstraction

## Summary

These guidelines help maintain code quality and consistency across the ProcessPlayground repository. They were derived from actual code review feedback on merged pull requests. Following these instructions will reduce the need for review iterations and help produce production-ready code from the start.
