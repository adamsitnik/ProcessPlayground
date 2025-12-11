package main

import (
	"io"
	"os/exec"
	"sync"
	"testing"
)

// BenchmarkDiscard_Sync benchmarks executing a process and discarding its output (synchronous).
// This redirects stdout and stderr to io.Discard (equivalent to /dev/null).
func BenchmarkDiscard_Sync(b *testing.B) {
	var exitCode int
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("dotnet", "--help")
		cmd.Stdout = io.Discard
		cmd.Stderr = io.Discard
		err := cmd.Run()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}
		exitCode = cmd.ProcessState.ExitCode()
	}
	_ = exitCode
}

// BenchmarkDiscard_WithWait benchmarks executing a process and discarding its output,
// using Start() followed by Wait().
func BenchmarkDiscard_WithWait(b *testing.B) {
	var exitCode int
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("dotnet", "--help")
		cmd.Stdout = io.Discard
		cmd.Stderr = io.Discard
		err := cmd.Start()
		if err != nil {
			b.Fatalf("Failed to start command: %v", err)
		}
		err = cmd.Wait()
		if err != nil {
			b.Fatalf("Failed to wait for command: %v", err)
		}
		exitCode = cmd.ProcessState.ExitCode()
	}
	_ = exitCode
}

// BenchmarkDiscard_ReadAndDiscard benchmarks executing a process where we read
// the output through pipes but immediately discard it (similar to C# event handler pattern).
func BenchmarkDiscard_ReadAndDiscard(b *testing.B) {
	var exitCode int
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("dotnet", "--help")
		stdout, err := cmd.StdoutPipe()
		if err != nil {
			b.Fatalf("Failed to create stdout pipe: %v", err)
		}
		stderr, err := cmd.StderrPipe()
		if err != nil {
			b.Fatalf("Failed to create stderr pipe: %v", err)
		}

		err = cmd.Start()
		if err != nil {
			b.Fatalf("Failed to start command: %v", err)
		}

		// Read and discard output with proper synchronization
		var wg sync.WaitGroup
		wg.Add(2)
		go func() {
			defer wg.Done()
			io.Copy(io.Discard, stdout)
		}()
		go func() {
			defer wg.Done()
			io.Copy(io.Discard, stderr)
		}()

		err = cmd.Wait()
		if err != nil {
			b.Fatalf("Failed to wait for command: %v", err)
		}
		wg.Wait()
		exitCode = cmd.ProcessState.ExitCode()
	}
	_ = exitCode
}
