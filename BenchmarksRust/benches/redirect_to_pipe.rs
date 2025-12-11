use criterion::{black_box, criterion_group, criterion_main, Criterion};
use std::io::{BufRead, BufReader, Read};
use std::process::{Command, Stdio};
use std::thread;

/// Benchmarks reading process output line-by-line using BufReader.
/// This is the most common pattern for reading process output in Rust.
fn redirect_to_pipe_lines(c: &mut Criterion) {
    c.bench_function("redirect_to_pipe_lines", |b| {
        b.iter(|| {
            let mut child = Command::new("dotnet")
                .arg("--help")
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .expect("Failed to spawn command");
            
            let stdout = child.stdout.take().expect("Failed to open stdout");
            let stderr = child.stderr.take().expect("Failed to open stderr");
            
            let stdout_reader = BufReader::new(stdout);
            let stderr_reader = BufReader::new(stderr);
            
            // Read stdout
            for line in stdout_reader.lines() {
                if let Ok(line) = line {
                    black_box(line);
                }
            }
            
            // Read stderr
            for line in stderr_reader.lines() {
                if let Ok(line) = line {
                    black_box(line);
                }
            }
            
            let status = child.wait().expect("Failed to wait for command");
            black_box(status.code());
        });
    });
}

/// Benchmarks using output() which reads all stdout (convenience method).
fn redirect_to_pipe_output(c: &mut Criterion) {
    c.bench_function("redirect_to_pipe_output", |b| {
        b.iter(|| {
            let output = Command::new("dotnet")
                .arg("--help")
                .output()
                .expect("Failed to execute command");
            
            black_box(&output.stdout);
            black_box(&output.stderr);
            black_box(output.status.code());
        });
    });
}

/// Benchmarks reading stdout and stderr concurrently
/// to avoid potential deadlocks with large outputs.
fn redirect_to_pipe_concurrent(c: &mut Criterion) {
    c.bench_function("redirect_to_pipe_concurrent", |b| {
        b.iter(|| {
            let mut child = Command::new("dotnet")
                .arg("--help")
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .expect("Failed to spawn command");
            
            let stdout = child.stdout.take().expect("Failed to open stdout");
            let stderr = child.stderr.take().expect("Failed to open stderr");
            
            // Read both streams concurrently
            let stdout_handle = thread::spawn(move || {
                let reader = BufReader::new(stdout);
                let mut lines = Vec::new();
                for line in reader.lines() {
                    if let Ok(line) = line {
                        lines.push(line);
                    }
                }
                lines
            });
            
            let stderr_handle = thread::spawn(move || {
                let reader = BufReader::new(stderr);
                let mut lines = Vec::new();
                for line in reader.lines() {
                    if let Ok(line) = line {
                        lines.push(line);
                    }
                }
                lines
            });
            
            let stdout_lines = stdout_handle.join().expect("Failed to join stdout thread");
            let stderr_lines = stderr_handle.join().expect("Failed to join stderr thread");
            
            let status = child.wait().expect("Failed to wait for command");
            
            black_box(stdout_lines);
            black_box(stderr_lines);
            black_box(status.code());
        });
    });
}

/// Benchmarks reading entire output at once using read_to_end.
fn redirect_to_pipe_read_all(c: &mut Criterion) {
    c.bench_function("redirect_to_pipe_read_all", |b| {
        b.iter(|| {
            let mut child = Command::new("dotnet")
                .arg("--help")
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .expect("Failed to spawn command");
            
            let mut stdout = child.stdout.take().expect("Failed to open stdout");
            let mut stderr = child.stderr.take().expect("Failed to open stderr");
            
            let mut stdout_buf = Vec::new();
            let mut stderr_buf = Vec::new();
            
            stdout.read_to_end(&mut stdout_buf).expect("Failed to read stdout");
            stderr.read_to_end(&mut stderr_buf).expect("Failed to read stderr");
            
            let status = child.wait().expect("Failed to wait for command");
            
            black_box(stdout_buf);
            black_box(stderr_buf);
            black_box(status.code());
        });
    });
}

criterion_group!(
    benches,
    redirect_to_pipe_lines,
    redirect_to_pipe_output,
    redirect_to_pipe_concurrent,
    redirect_to_pipe_read_all
);
criterion_main!(benches);
