use criterion::{black_box, criterion_group, criterion_main, Criterion};
use std::process::Command;

/// Benchmarks executing a process without output redirection (synchronous).
/// The child process inherits the parent's standard handles.
fn no_redirection_sync(c: &mut Criterion) {
    c.bench_function("no_redirection_sync", |b| {
        b.iter(|| {
            let output = Command::new("dotnet")
                .arg("--help")
                .status()
                .expect("Failed to execute command");
            black_box(output.code());
        });
    });
}

/// Benchmarks executing a process without output redirection,
/// using spawn() followed by wait() (equivalent to Start + Wait in C#).
fn no_redirection_with_wait(c: &mut Criterion) {
    c.bench_function("no_redirection_with_wait", |b| {
        b.iter(|| {
            let mut child = Command::new("dotnet")
                .arg("--help")
                .spawn()
                .expect("Failed to spawn command");
            let status = child.wait().expect("Failed to wait for command");
            black_box(status.code());
        });
    });
}

/// Benchmarks executing a process with explicitly inheriting standard handles.
fn no_redirection_explicit_inherit(c: &mut Criterion) {
    c.bench_function("no_redirection_explicit_inherit", |b| {
        b.iter(|| {
            let output = Command::new("dotnet")
                .arg("--help")
                .stdout(std::process::Stdio::inherit())
                .stderr(std::process::Stdio::inherit())
                .status()
                .expect("Failed to execute command");
            black_box(output.code());
        });
    });
}

criterion_group!(
    benches,
    no_redirection_sync,
    no_redirection_with_wait,
    no_redirection_explicit_inherit
);
criterion_main!(benches);
