package main

import (
	"os"
	"os/exec"
	"testing"
)

// BenchmarkNoRedirection_Sync benchmarks executing a process without output redirection (synchronous).
// The child process inherits the parent's standard handles.
func BenchmarkNoRedirection_Sync(b *testing.B) {
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("go", "version")
		// Not setting Stdout/Stderr means the child inherits parent's handles
		err := cmd.Run()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}
	}
}

// BenchmarkNoRedirection_WithWait benchmarks executing a process without output redirection,
// using Start() followed by Wait().
func BenchmarkNoRedirection_WithWait(b *testing.B) {
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("go", "version")
		// Not setting Stdout/Stderr means the child inherits parent's handles
		err := cmd.Start()
		if err != nil {
			b.Fatalf("Failed to start command: %v", err)
		}
		err = cmd.Wait()
		if err != nil {
			b.Fatalf("Failed to wait for command: %v", err)
		}
	}
}

// BenchmarkNoRedirection_ExplicitInherit benchmarks executing a process with explicitly
// inheriting standard handles.
func BenchmarkNoRedirection_ExplicitInherit(b *testing.B) {
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("go", "version")
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		err := cmd.Run()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}
	}
}
