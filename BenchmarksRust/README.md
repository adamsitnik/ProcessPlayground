# Rust Process Benchmarks

This directory contains Rust benchmarks that mirror the C# benchmarks in the parent `Benchmarks` directory. These benchmarks measure the performance of different process execution patterns in Rust.

## Overview

The benchmarks are organized into four categories:

### 1. NoRedirection (`benches/no_redirection.rs`)
Benchmarks for executing processes without redirecting their output. The child process inherits the parent's standard handles (stdin, stdout, stderr).

**Benchmarks:**
- `no_redirection_sync` - Using `Command::status()` (synchronous)
- `no_redirection_with_wait` - Using `spawn()` + `wait()`
- `no_redirection_explicit_inherit` - Explicitly setting stdout/stderr to inherit

### 2. Discard (`benches/discard.rs`)
Benchmarks for executing processes and discarding their output (equivalent to redirecting to `/dev/null`).

**Benchmarks:**
- `discard_sync` - Using `Stdio::null()` with `status()`
- `discard_with_wait` - Using `Stdio::null()` with `spawn()` + `wait()`
- `discard_read_and_discard` - Reading through pipes but discarding content

### 3. RedirectToFile (`benches/redirect_to_file.rs`)
Benchmarks for redirecting process output to files.

**Benchmarks:**
- `redirect_to_file_direct` - Direct file redirection (most efficient)
- `redirect_to_file_through_pipe` - Reading from pipe and writing to file
- `redirect_to_file_shell` - Using shell redirection (`sh -c "cmd > file"`)

### 4. RedirectToPipe (`benches/redirect_to_pipe.rs`)
Benchmarks for reading process output through pipes.

**Benchmarks:**
- `redirect_to_pipe_lines` - Line-by-line reading with `BufReader`
- `redirect_to_pipe_output` - Using `Command::output()` (convenience method)
- `redirect_to_pipe_concurrent` - Reading stdout and stderr concurrently
- `redirect_to_pipe_read_all` - Reading entire output with `read_to_end()`

## Prerequisites

- **Rust 1.70 or later** installed on your system
- The `cargo` command must be available in your PATH
- The `dotnet` command must be available in your PATH (used by benchmarks)

To check if Rust is installed:
```bash
rustc --version
cargo --version
```

If Rust is not installed, download it from: https://rustup.rs/

## Running the Benchmarks

### Quick Start

To run all benchmarks:

```bash
cd BenchmarksRust
cargo bench
```

This will compile the benchmarks in release mode (with optimizations) and run all of them.

### Running Specific Benchmark Categories

Run only NoRedirection benchmarks:
```bash
cargo bench --bench no_redirection
```

Run only Discard benchmarks:
```bash
cargo bench --bench discard
```

Run only RedirectToFile benchmarks:
```bash
cargo bench --bench redirect_to_file
```

Run only RedirectToPipe benchmarks:
```bash
cargo bench --bench redirect_to_pipe
```

### Running a Specific Benchmark

Run a single benchmark by name pattern:
```bash
cargo bench --bench no_redirection -- no_redirection_sync
```

The `--` separator tells cargo to pass the following arguments to the benchmark binary.

### Benchmark Name Filtering

Criterion (the benchmarking framework) supports filtering benchmarks by name using regex patterns:

```bash
# Run all benchmarks containing "sync" in the name
cargo bench -- sync

# Run all benchmarks containing "pipe" in the name
cargo bench -- pipe

# Run all benchmarks in a specific file that contain "direct"
cargo bench --bench redirect_to_file -- direct

# Run multiple specific benchmarks using regex alternation
cargo bench -- "sync|concurrent"
```

### Best Practices for Accurate Benchmarking

#### 1. Always Use Release Mode

Cargo bench automatically runs in release mode with optimizations enabled. **Never** run benchmarks in debug mode, as performance will be dramatically slower and not representative.

To verify release mode is being used:
```bash
# cargo bench automatically uses --release
cargo bench

# This is what happens under the hood:
# cargo build --release --benches
```

#### 2. Disable CPU Frequency Scaling (Linux)

For more consistent results on Linux:

```bash
# Temporarily disable CPU frequency scaling
sudo cpupower frequency-set --governor performance

# Run benchmarks
cargo bench

# Re-enable (optional)
sudo cpupower frequency-set --governor powersave
```

#### 3. Close Other Applications

Close unnecessary applications and processes to reduce system noise during benchmarking.

#### 4. Increase Sample Size

Criterion automatically adjusts the sample size for statistical significance, but you can control it:

```bash
# Run with more samples (default is usually 100)
cargo bench -- --sample-size 1000

# Set measurement time (default is 5 seconds per benchmark)
cargo bench -- --measurement-time 10
```

#### 5. Save and Compare Results

Criterion automatically saves baseline results and compares subsequent runs:

```bash
# First run establishes baseline
cargo bench

# Make changes to code...

# Second run compares against baseline
cargo bench

# Save current results as a named baseline
cargo bench -- --save-baseline my_baseline

# Compare against a specific baseline
cargo bench -- --baseline my_baseline
```

#### 6. Generate HTML Reports

Criterion generates detailed HTML reports by default:

```bash
cargo bench

# Reports are saved to target/criterion/
# Open target/criterion/report/index.html in a browser
```

On Linux/macOS:
```bash
# Open the report in your default browser
xdg-open target/criterion/report/index.html  # Linux
open target/criterion/report/index.html       # macOS
```

On Windows:
```cmd
start target\criterion\report\index.html
```

### Understanding Benchmark Output

Example output:
```
no_redirection_sync     time:   [245.67 ms 248.32 ms 251.45 ms]
                        change: [-2.5431% +0.1234% +2.8901%] (p = 0.89 > 0.05)
                        No change in performance detected.
```

- `no_redirection_sync` - Benchmark name
- `time: [245.67 ms 248.32 ms 251.45 ms]` - Lower bound, estimate, upper bound
- `change: [...]` - Performance change compared to previous run
- `p = 0.89 > 0.05` - Statistical significance (p > 0.05 means not significant)

#### Time Units

Criterion automatically chooses the most readable time unit:
- `ns` - nanoseconds (0.000000001 seconds)
- `μs` or `us` - microseconds (0.000001 seconds)
- `ms` - milliseconds (0.001 seconds)
- `s` - seconds

For process benchmarks, times are typically in the 200-300 millisecond range.

### Command-Line Options

Common options for `cargo bench`:

| Option | Description | Example |
|--------|-------------|---------|
| No arguments | Run all benchmarks | `cargo bench` |
| `--bench NAME` | Run specific benchmark file | `cargo bench --bench no_redirection` |
| `-- FILTER` | Filter by benchmark name | `cargo bench -- sync` |
| `-- --sample-size N` | Set sample size | `cargo bench -- --sample-size 200` |
| `-- --measurement-time N` | Measurement time in seconds | `cargo bench -- --measurement-time 10` |
| `-- --save-baseline NAME` | Save results as baseline | `cargo bench -- --save-baseline v1.0` |
| `-- --baseline NAME` | Compare against baseline | `cargo bench -- --baseline v1.0` |
| `-- --list` | List available benchmarks | `cargo bench -- --list` |
| `-- --help` | Show help for benchmark options | `cargo bench -- --help` |

### Profiling Benchmarks

#### Using perf (Linux)

```bash
# Install perf
sudo apt-get install linux-tools-common linux-tools-generic

# Profile a specific benchmark
cargo bench --bench no_redirection --profile-time 10

# Or use perf directly
perf record -g cargo bench --bench no_redirection -- --profile-time 1
perf report
```

#### Using Instruments (macOS)

```bash
# Build the benchmark binary
cargo bench --no-run

# Find the binary in target/release/deps/
# Run with Instruments
instruments -t "Time Profiler" target/release/deps/no_redirection-<hash>
```

#### Using flamegraph

```bash
# Install cargo-flamegraph
cargo install flamegraph

# Generate flamegraph (requires sudo on Linux for perf access)
cargo flamegraph --bench no_redirection
```

## Design Notes

These Rust benchmarks are designed to be equivalent to the C# benchmarks in the parent `Benchmarks` directory, using Rust's standard library:

- **C# `Process.Start()` ↔ Rust `Command::status()`** - Synchronous process execution
- **C# `Process.WaitForExitAsync()` ↔ Rust `spawn() + wait()`** - Spawn and wait pattern
- **C# `/dev/null` redirection ↔ Rust `Stdio::null()`** - Discarding output
- **C# File redirection ↔ Rust `File` + `Stdio::from()`** - File I/O
- **C# Pipe reading ↔ Rust `StdoutPipe()` + `BufReader`** - Reading output

### Key Differences from C#

1. **Rust uses ownership system** - Handles are moved, not copied
2. **No built-in async/await for processes** - Use threads for concurrency
3. **Rust's `Command` is simpler** - Less configuration needed compared to `ProcessStartInfo`
4. **Rust uses `Stdio::null()`** - Built-in null device handling
5. **Rust uses Criterion** - Different benchmarking framework than BenchmarkDotNet

### Benchmarking Framework: Criterion

Rust uses [Criterion.rs](https://github.com/bheisler/criterion.rs) for benchmarking, which provides:

- **Statistical analysis** - Detects performance changes with confidence intervals
- **Automatic sample size selection** - Runs enough iterations for significance
- **HTML reports** - Detailed charts and statistics
- **Baseline comparison** - Track performance over time
- **Outlier detection** - Identifies and handles noisy measurements

Criterion is the de facto standard for Rust benchmarks and is more feature-rich than Go's built-in benchmarking.

## Troubleshooting

### "cargo: command not found"
Install Rust from https://rustup.rs/ and ensure it's in your PATH.

### "dotnet: command not found"
Install the .NET SDK from https://dotnet.microsoft.com/download and ensure it's in your PATH.

### Benchmarks fail with "permission denied"
On Unix systems, ensure benchmark binaries have execute permissions:
```bash
chmod +x target/release/deps/no_redirection-*
```

### Compilation errors
Make sure you're using Rust 1.70 or later:
```bash
rustup update
```

### High variance in results
- Close other applications
- Run multiple times and compare baselines
- Consider disabling CPU frequency scaling (Linux)
- Increase sample size: `cargo bench -- --sample-size 200`

### "too many open files" error
Increase file descriptor limit:
```bash
ulimit -n 4096
```

### Benchmarks take too long
Reduce sample size or measurement time:
```bash
cargo bench -- --sample-size 50 --measurement-time 3
```

### Cannot open HTML reports
The reports are generated in `target/criterion/report/index.html`. You may need to:
- Check file permissions
- Use a file browser to navigate to the file
- Copy the file to a web-accessible location

## Additional Resources

- [Criterion.rs Documentation](https://bheisler.github.io/criterion.rs/book/)
- [Rust std::process Documentation](https://doc.rust-lang.org/std/process/)
- [The Rust Programming Language Book](https://doc.rust-lang.org/book/)
- [Rust Performance Book](https://nnethercote.github.io/perf-book/)
- [cargo-flamegraph](https://github.com/flamegraph-rs/flamegraph)
