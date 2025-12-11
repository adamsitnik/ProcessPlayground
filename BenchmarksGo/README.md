# Go Process Benchmarks

This directory contains Go benchmarks that mirror the C# benchmarks in the parent `Benchmarks` directory. These benchmarks measure the performance of different process execution patterns in Go.

## Overview

The benchmarks are organized into four categories:

### 1. NoRedirection (`no_redirection_test.go`)
Benchmarks for executing processes without redirecting their output. The child process inherits the parent's standard handles (stdin, stdout, stderr).

**Benchmarks:**
- `BenchmarkNoRedirection_Sync` - Using `cmd.Run()` (synchronous)
- `BenchmarkNoRedirection_WithWait` - Using `cmd.Start()` + `cmd.Wait()`
- `BenchmarkNoRedirection_ExplicitInherit` - Explicitly setting stdout/stderr to parent's handles

### 2. Discard (`discard_test.go`)
Benchmarks for executing processes and discarding their output (equivalent to redirecting to `/dev/null`).

**Benchmarks:**
- `BenchmarkDiscard_Sync` - Using `io.Discard` with `cmd.Run()`
- `BenchmarkDiscard_WithWait` - Using `io.Discard` with `cmd.Start()` + `cmd.Wait()`
- `BenchmarkDiscard_ReadAndDiscard` - Reading through pipes but discarding content

### 3. RedirectToFile (`redirect_to_file_test.go`)
Benchmarks for redirecting process output to files.

**Benchmarks:**
- `BenchmarkRedirectToFile_Direct` - Direct file redirection (most efficient)
- `BenchmarkRedirectToFile_ThroughPipe` - Reading from pipe and writing to file
- `BenchmarkRedirectToFile_Shell` - Using shell redirection (`sh -c "cmd > file"`)

### 4. RedirectToPipe (`redirect_to_pipe_test.go`)
Benchmarks for reading process output through pipes.

**Benchmarks:**
- `BenchmarkRedirectToPipe_Scanner` - Line-by-line reading with `bufio.Scanner`
- `BenchmarkRedirectToPipe_CombinedOutput` - Using `cmd.CombinedOutput()`
- `BenchmarkRedirectToPipe_Output` - Using `cmd.Output()` (stdout only)
- `BenchmarkRedirectToPipe_Concurrent` - Reading stdout and stderr concurrently
- `BenchmarkRedirectToPipe_ReadAll` - Reading entire output with `io.ReadAll()`

## Prerequisites

- **Go 1.16 or later** installed on your system
- The `go` command must be available in your PATH (used by benchmarks)

To check if Go is installed:
```bash
go version
```

If Go is not installed, download it from: https://go.dev/dl/

## Running the Benchmarks

### Quick Start

To run all benchmarks:

```bash
cd BenchmarksGo
go test -bench="." -benchmem
```

### Running Specific Benchmark Categories

Run only NoRedirection benchmarks:
```bash
go test -bench=NoRedirection -benchmem
```

Run only Discard benchmarks:
```bash
go test -bench=Discard -benchmem
```

Run only RedirectToFile benchmarks:
```bash
go test -bench=RedirectToFile -benchmem
```

Run only RedirectToPipe benchmarks:
```bash
go test -bench=RedirectToPipe -benchmem
```

### Running a Specific Benchmark

Run a single benchmark by its full name:
```bash
go test -bench=BenchmarkNoRedirection_Sync -benchmem
```

### Benchmark Name Filtering with Regular Expressions

The `-bench` flag accepts Go regular expressions to filter benchmarks by name. The pattern matches against the full benchmark name.

**Important**: The `.` (dot) is a regex wildcard that matches any character, so `-bench=.` matches all benchmarks.

**Platform-specific note**: On Windows (cmd.exe or PowerShell), the dot needs to be quoted:
```cmd
go test -bench="." -benchmem
```

On Unix/Linux/macOS, quotes are optional but harmless:
```bash
go test -bench=. -benchmem
# or
go test -bench="." -benchmem
```

**Common filtering patterns:**

```bash
# Run all benchmarks (dot matches any character)
go test -bench="." -benchmem

# Run all Discard benchmarks (any benchmark with "Discard" in the name)
go test -bench=Discard -benchmem

# Run only synchronous benchmarks (those ending with "_Sync")
go test -bench=_Sync$ -benchmem

# Run all "Redirect" benchmarks (RedirectToFile and RedirectToPipe)
go test -bench=Redirect -benchmem

# Run benchmarks that use pipes (contains "Pipe" in the name)
go test -bench=Pipe -benchmem

# Run multiple specific benchmarks using alternation
go test -bench="Discard_Sync|NoRedirection_Sync" -benchmem

# Run benchmarks starting with a specific prefix
go test -bench=^BenchmarkDiscard -benchmem

# Exclude benchmarks (run everything except Shell benchmarks)
go test -bench="." -run=^$ | grep -v Shell
```

**Regex special characters:**
- `.` - Matches any single character
- `^` - Matches the beginning of the name
- `$` - Matches the end of the name
- `|` - Alternation (OR)
- `.*` - Matches any sequence of characters

### Best Practices for Accurate Benchmarking

#### 1. Run with Sufficient Iterations
By default, Go's benchmark framework will run benchmarks until it has enough data for statistical significance. You can control the minimum time:

```bash
go test -bench="." -benchtime=5s
```

This runs each benchmark for at least 5 seconds.

#### 2. Disable CPU Frequency Scaling (Linux)
For more consistent results on Linux:

```bash
# Temporarily disable CPU frequency scaling
sudo cpupower frequency-set --governor performance

# Run benchmarks
go test -bench="." -benchmem

# Re-enable (optional)
sudo cpupower frequency-set --governor powersave
```

#### 3. Close Other Applications
Close unnecessary applications and processes to reduce system noise during benchmarking.

#### 4. Run Multiple Times
Run benchmarks multiple times and compare results using `benchstat`:

```bash
# Install benchstat
go install golang.org/x/perf/cmd/benchstat@latest

# Run benchmarks multiple times
go test -bench="." -benchmem -count=10 > results.txt

# Analyze results
benchstat results.txt
```

#### 5. Compare Before/After
To compare performance changes:

```bash
# Baseline
go test -bench="." -benchmem -count=10 > old.txt

# Make changes...

# New results
go test -bench="." -benchmem -count=10 > new.txt

# Compare
benchstat old.txt new.txt
```

### Understanding Benchmark Output

Example output:
```
BenchmarkNoRedirection_Sync-8     1000  1234567 ns/op  12345 B/op  123 allocs/op
```

- `BenchmarkNoRedirection_Sync` - Benchmark name
- `-8` - Number of CPU cores used (GOMAXPROCS)
- `1000` - Number of iterations run
- `1234567 ns/op` - Average time per operation in nanoseconds
- `12345 B/op` - Average bytes allocated per operation
- `123 allocs/op` - Average number of allocations per operation

#### Time Units

Go's benchmark framework automatically chooses the time unit (ns, μs, ms, s) based on the magnitude of the benchmark duration. The framework will display times in the most readable unit. However, the raw output is always in nanoseconds.

To view results in different time units, use `benchstat` for better formatted output with appropriate units:
```bash
go test -bench=. -count=10 > results.txt
benchstat results.txt
```

For the process benchmarks in this project (which typically run for ~200-300ms), the output will usually show times in nanoseconds when each operation takes millions of nanoseconds. You can verify this by running:
```bash
go test -bench="." -benchtime=1s
```

#### Exporting Benchmark Results

**Important**: Go's `go test` does not have a native flag like `--export` to save benchmark results to a separate file. Since some benchmarks in this project write to standard output (e.g., NoRedirection benchmarks), any output redirection will mix benchmark results with process output.

**Best solution: Run only benchmarks that don't print to stdout**
```bash
# Run only benchmarks that redirect or discard output (cleanest results)
go test -bench="Discard|RedirectToFile|RedirectToPipe" -benchmem > results.txt
```

**Alternative solutions if you need to run all benchmarks:**

**Method 1: Redirect stderr separately (Unix/Linux/macOS)**
```bash
# Benchmark results go to stdout, process output goes to stderr
go test -bench="." -benchmem > results.txt 2> process_output.txt
```
Note: This doesn't fully separate them since benchmark framework also writes to stderr.

**Method 2: Filter benchmark lines after the fact**
```bash
go test -bench="." -benchmem 2>&1 | grep "^Benchmark" > results.txt
```
This captures only lines starting with "Benchmark", but loses context (goos, goarch, pkg info).

**Method 3: Save everything and parse later**
```bash
# Save all output
go test -bench="." -benchmem > full_output.txt 2>&1

# Extract benchmark lines manually or use benchstat
grep "^Benchmark" full_output.txt > results.txt
```

**For analysis with benchstat:**
```bash
# Run multiple times for statistical analysis
go test -bench="Discard|RedirectToFile|RedirectToPipe" -benchmem -count=10 > results.txt
benchstat results.txt
```

### Command-Line Options

Common flags for `go test`:

| Flag | Description | Example |
|------|-------------|---------|
| `-bench="."` | Run all benchmarks | `go test -bench="."` |
| `-bench=Pattern` | Run benchmarks matching pattern | `go test -bench=NoRedirection` |
| `-benchmem` | Show memory allocation statistics | `go test -bench="." -benchmem` |
| `-benchtime=Xs` | Run each benchmark for X seconds | `go test -bench="." -benchtime=5s` |
| `-count=N` | Run each benchmark N times | `go test -bench="." -count=10` |
| `-cpu=1,2,4` | Run benchmarks with different GOMAXPROCS | `go test -bench="." -cpu=1,2,4` |
| `-timeout=Xm` | Set overall timeout | `go test -bench="." -timeout=30m` |

### Profiling Benchmarks

#### CPU Profile
```bash
go test -bench="." -cpuprofile=cpu.prof
go tool pprof cpu.prof
```

#### Memory Profile
```bash
go test -bench="." -memprofile=mem.prof
go tool pprof mem.prof
```

#### Trace
```bash
go test -bench="." -trace=trace.out
go tool trace trace.out
```

## Design Notes

These Go benchmarks are designed to be equivalent to the C# benchmarks in the parent `Benchmarks` directory, using Go's standard library:

- **C# `Process.Start()` ↔ Go `exec.Command().Run()`** - Synchronous process execution
- **C# `Process.WaitForExitAsync()` ↔ Go `cmd.Start() + cmd.Wait()`** - Asynchronous pattern
- **C# `/dev/null` redirection ↔ Go `io.Discard`** - Discarding output
- **C# File redirection ↔ Go `os.File`** - File I/O
- **C# Pipe reading ↔ Go `StdoutPipe()` + `bufio.Scanner`** - Reading output

### Key Differences from C#

1. **Go doesn't have built-in async/await** - Go uses goroutines and channels instead
2. **Go's `exec.Command` is simpler** - Less configuration needed compared to `ProcessStartInfo`
3. **Go uses `io.Discard`** instead of explicit `/dev/null` handling
4. **Go's benchmark framework is built-in** - No need for external packages like BenchmarkDotNet

## Troubleshooting

### "go: command not found"
Install Go from https://go.dev/dl/ and ensure it's in your PATH.

### Benchmarks run too quickly
Increase benchmark time:
```bash
go test -bench="." -benchtime=10s
```

### High variance in results
- Close other applications
- Run multiple iterations with `-count=N`
- Use `benchstat` to analyze statistical significance
- Consider disabling CPU frequency scaling

### "too many open files" error
Increase file descriptor limit:
```bash
ulimit -n 4096
```

## Additional Resources

- [Go Testing Package Documentation](https://pkg.go.dev/testing)
- [How to Write Benchmarks in Go](https://dave.cheney.net/2013/06/30/how-to-write-benchmarks-in-go)
- [Go Performance Tips](https://github.com/dgryski/go-perfbook)
- [benchstat Tool](https://pkg.go.dev/golang.org/x/perf/cmd/benchstat)
