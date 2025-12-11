package main

import (
	"bufio"
	"io"
	"os/exec"
	"testing"
)

// BenchmarkRedirectToPipe_Scanner benchmarks reading process output line-by-line using bufio.Scanner.
// This is the most common pattern for reading process output in Go.
func BenchmarkRedirectToPipe_Scanner(b *testing.B) {
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

		// Read stdout line by line
		stdoutScanner := bufio.NewScanner(stdout)
		for stdoutScanner.Scan() {
			_ = stdoutScanner.Text()
		}

		// Read stderr line by line
		stderrScanner := bufio.NewScanner(stderr)
		for stderrScanner.Scan() {
			_ = stderrScanner.Text()
		}

		err = cmd.Wait()
		if err != nil {
			b.Fatalf("Failed to wait for command: %v", err)
		}
		exitCode = cmd.ProcessState.ExitCode()
	}
	_ = exitCode
}

// BenchmarkRedirectToPipe_CombinedOutput benchmarks using CombinedOutput() which
// reads both stdout and stderr together.
func BenchmarkRedirectToPipe_CombinedOutput(b *testing.B) {
	var exitCode int
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("dotnet", "--help")
		output, err := cmd.CombinedOutput()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}
		_ = output
		exitCode = cmd.ProcessState.ExitCode()
	}
	_ = exitCode
}

// BenchmarkRedirectToPipe_Output benchmarks using Output() which reads stdout only.
func BenchmarkRedirectToPipe_Output(b *testing.B) {
	var exitCode int
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("dotnet", "--help")
		output, err := cmd.Output()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}
		_ = output
		exitCode = cmd.ProcessState.ExitCode()
	}
	_ = exitCode
}

// BenchmarkRedirectToPipe_Concurrent benchmarks reading stdout and stderr concurrently
// to avoid potential deadlocks with large outputs.
func BenchmarkRedirectToPipe_Concurrent(b *testing.B) {
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

		// Read both streams concurrently
		done := make(chan error, 2)

		go func() {
			scanner := bufio.NewScanner(stdout)
			for scanner.Scan() {
				_ = scanner.Text()
			}
			done <- scanner.Err()
		}()

		go func() {
			scanner := bufio.NewScanner(stderr)
			for scanner.Scan() {
				_ = scanner.Text()
			}
			done <- scanner.Err()
		}()

		// Wait for both readers
		for j := 0; j < 2; j++ {
			if err := <-done; err != nil {
				b.Fatalf("Failed to read output: %v", err)
			}
		}

		err = cmd.Wait()
		if err != nil {
			b.Fatalf("Failed to wait for command: %v", err)
		}
		exitCode = cmd.ProcessState.ExitCode()
	}
	_ = exitCode
}

// BenchmarkRedirectToPipe_ReadAll benchmarks reading entire output at once using io.ReadAll.
func BenchmarkRedirectToPipe_ReadAll(b *testing.B) {
	var exitCode int
	for i := 0; i < b.N; i++ {
		cmd := exec.Command("dotnet", "--help")
		stdout, err := cmd.StdoutPipe()
		if err != nil {
			b.Fatalf("Failed to create stdout pipe: %v", err)
		}

		err = cmd.Start()
		if err != nil {
			b.Fatalf("Failed to start command: %v", err)
		}

		output, err := io.ReadAll(stdout)
		if err != nil {
			b.Fatalf("Failed to read output: %v", err)
		}
		_ = output

		err = cmd.Wait()
		if err != nil {
			b.Fatalf("Failed to wait for command: %v", err)
		}
		exitCode = cmd.ProcessState.ExitCode()
	}
	_ = exitCode
}
