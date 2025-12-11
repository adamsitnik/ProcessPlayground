package main

import (
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"
)

// BenchmarkRedirectToFile_Direct benchmarks redirecting output directly to a file.
// This is the most efficient method as the OS handles the redirection.
func BenchmarkRedirectToFile_Direct(b *testing.B) {
	tmpDir := b.TempDir()
	filePath := filepath.Join(tmpDir, "output.txt")

	var exitCode int
	for i := 0; i < b.N; i++ {
		file, err := os.Create(filePath)
		if err != nil {
			b.Fatalf("Failed to create file: %v", err)
		}

		cmd := exec.Command("dotnet", "--help")
		cmd.Stdout = file
		cmd.Stderr = file

		err = cmd.Run()
		file.Close()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}
		exitCode = cmd.ProcessState.ExitCode()

		// Stop the timer during cleanup
		b.StopTimer()
		os.Remove(filePath)
		b.StartTimer()
	}
	_ = exitCode
}

// BenchmarkRedirectToFile_ThroughPipe benchmarks reading from a pipe and writing to a file.
// This is less efficient as it requires reading through Go and then writing.
func BenchmarkRedirectToFile_ThroughPipe(b *testing.B) {
	tmpDir := b.TempDir()
	filePath := filepath.Join(tmpDir, "output.txt")

	var exitCode int
	for i := 0; i < b.N; i++ {
		file, err := os.Create(filePath)
		if err != nil {
			b.Fatalf("Failed to create file: %v", err)
		}

		cmd := exec.Command("dotnet", "--help")
		stdout, err := cmd.StdoutPipe()
		if err != nil {
			b.Fatalf("Failed to create stdout pipe: %v", err)
		}

		err = cmd.Start()
		if err != nil {
			b.Fatalf("Failed to start command: %v", err)
		}

		// Copy output to file
		_, err = io.Copy(file, stdout)
		if err != nil {
			b.Fatalf("Failed to copy output: %v", err)
		}

		file.Close()

		err = cmd.Wait()
		if err != nil {
			b.Fatalf("Failed to wait for command: %v", err)
		}
		exitCode = cmd.ProcessState.ExitCode()

		// Stop the timer during cleanup
		b.StopTimer()
		os.Remove(filePath)
		b.StartTimer()
	}
	_ = exitCode
}

// BenchmarkRedirectToFile_Shell benchmarks using shell redirection.
// This spawns a shell which then handles the redirection.
func BenchmarkRedirectToFile_Shell(b *testing.B) {
	tmpDir := b.TempDir()
	filePath := filepath.Join(tmpDir, "output.txt")

	var exitCode int
	for i := 0; i < b.N; i++ {
		var cmd *exec.Cmd
		if runtime.GOOS == "windows" {
			// Windows: Use cmd.exe with proper quoting
			cmd = exec.Command("cmd", "/c", "dotnet --help > \""+filePath+"\"")
		} else {
			// Unix/Linux/macOS: Use sh with proper quoting
			cmd = exec.Command("sh", "-c", "dotnet --help > '"+filePath+"'")
		}
		err := cmd.Run()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}
		exitCode = cmd.ProcessState.ExitCode()

		// Stop the timer during cleanup
		b.StopTimer()
		os.Remove(filePath)
		b.StartTimer()
	}
	_ = exitCode
}
