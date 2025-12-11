use criterion::{black_box, criterion_group, criterion_main, Criterion};
use std::fs::File;
use std::io::{BufRead, BufReader, Write};
use std::process::{Command, Stdio};

/// Benchmarks redirecting output directly to a file.
/// This is the most efficient method as the OS handles the redirection.
fn redirect_to_file_direct(c: &mut Criterion) {
    c.bench_function("redirect_to_file_direct", |b| {
        let temp_dir = std::env::temp_dir();
        let file_path = temp_dir.join("rust_bench_output.txt");
        
        b.iter(|| {
            let file = File::create(&file_path).expect("Failed to create file");
            
            let output = Command::new("dotnet")
                .arg("--help")
                .stdout(file.try_clone().expect("Failed to clone file handle"))
                .stderr(file)
                .status()
                .expect("Failed to execute command");
            
            black_box(output.code());
            
            // Cleanup
            let _ = std::fs::remove_file(&file_path);
        });
    });
}

/// Benchmarks reading from a pipe and writing to a file.
/// This is less efficient as it requires reading through Rust and then writing.
fn redirect_to_file_through_pipe(c: &mut Criterion) {
    c.bench_function("redirect_to_file_through_pipe", |b| {
        let temp_dir = std::env::temp_dir();
        let file_path = temp_dir.join("rust_bench_output.txt");
        
        b.iter(|| {
            let mut file = File::create(&file_path).expect("Failed to create file");
            
            let mut child = Command::new("dotnet")
                .arg("--help")
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .expect("Failed to spawn command");
            
            let stdout = child.stdout.take().expect("Failed to open stdout");
            let reader = BufReader::new(stdout);
            
            for line in reader.lines() {
                if let Ok(line) = line {
                    writeln!(file, "{}", line).expect("Failed to write to file");
                }
            }
            
            let status = child.wait().expect("Failed to wait for command");
            black_box(status.code());
            
            // Cleanup
            let _ = std::fs::remove_file(&file_path);
        });
    });
}

/// Benchmarks using shell redirection.
/// This spawns a shell which then handles the redirection.
fn redirect_to_file_shell(c: &mut Criterion) {
    c.bench_function("redirect_to_file_shell", |b| {
        let temp_dir = std::env::temp_dir();
        let file_path = temp_dir.join("rust_bench_output.txt");
        
        b.iter(|| {
            #[cfg(target_os = "windows")]
            let output = {
                let cmd_str = format!("dotnet --help > \"{}\"", file_path.display());
                Command::new("cmd")
                    .args(&["/c", &cmd_str])
                    .status()
                    .expect("Failed to execute command")
            };
            
            #[cfg(not(target_os = "windows"))]
            let output = {
                let cmd_str = format!("dotnet --help > '{}'", file_path.display());
                Command::new("sh")
                    .args(&["-c", &cmd_str])
                    .status()
                    .expect("Failed to execute command")
            };
            
            black_box(output.code());
            
            // Cleanup
            let _ = std::fs::remove_file(&file_path);
        });
    });
}

criterion_group!(
    benches,
    redirect_to_file_direct,
    redirect_to_file_through_pipe,
    redirect_to_file_shell
);
criterion_main!(benches);
