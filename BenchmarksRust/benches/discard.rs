use criterion::{black_box, criterion_group, criterion_main, Criterion};
use std::process::{Command, Stdio};

/// Benchmarks executing a process and discarding its output (synchronous).
/// This redirects stdout and stderr to null (equivalent to /dev/null).
fn discard_sync(c: &mut Criterion) {
    c.bench_function("discard_sync", |b| {
        b.iter(|| {
            let output = Command::new("dotnet")
                .arg("--help")
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .status()
                .expect("Failed to execute command");
            black_box(output.code());
        });
    });
}

/// Benchmarks executing a process and discarding its output,
/// using spawn() followed by wait().
fn discard_with_wait(c: &mut Criterion) {
    c.bench_function("discard_with_wait", |b| {
        b.iter(|| {
            let mut child = Command::new("dotnet")
                .arg("--help")
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .spawn()
                .expect("Failed to spawn command");
            let status = child.wait().expect("Failed to wait for command");
            black_box(status.code());
        });
    });
}

/// Benchmarks executing a process where we capture the output through pipes
/// but immediately discard it (similar to C# event handler pattern).
fn discard_read_and_discard(c: &mut Criterion) {
    c.bench_function("discard_read_and_discard", |b| {
        b.iter(|| {
            let child = Command::new("dotnet")
                .arg("--help")
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .expect("Failed to spawn command");

            // Read and discard the output
            let output = child
                .wait_with_output()
                .expect("Failed to wait for command");
            
            // Discard the output by just accessing it
            black_box(output.stdout.len());
            black_box(output.stderr.len());
            black_box(output.status.code());
        });
    });
}

criterion_group!(benches, discard_sync, discard_with_wait, discard_read_and_discard);
criterion_main!(benches);
