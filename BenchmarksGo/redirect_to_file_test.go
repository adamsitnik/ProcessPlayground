package main

import (
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

// BenchmarkRedirectToFile_Direct benchmarks redirecting output directly to a file.
// This is the most efficient method as the OS handles the redirection.
func BenchmarkRedirectToFile_Direct(b *testing.B) {
	tmpDir := b.TempDir()
	filePath := filepath.Join(tmpDir, "output.txt")

	for i := 0; i < b.N; i++ {
		file, err := os.Create(filePath)
		if err != nil {
			b.Fatalf("Failed to create file: %v", err)
		}

		cmd := exec.Command("go", "version")
		cmd.Stdout = file
		cmd.Stderr = file

		err = cmd.Run()
		file.Close()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}

		// Clean up for next iteration
		os.Remove(filePath)
	}
}

// BenchmarkRedirectToFile_ThroughPipe benchmarks reading from a pipe and writing to a file.
// This is less efficient as it requires reading through Go and then writing.
func BenchmarkRedirectToFile_ThroughPipe(b *testing.B) {
	tmpDir := b.TempDir()
	filePath := filepath.Join(tmpDir, "output.txt")

	for i := 0; i < b.N; i++ {
		file, err := os.Create(filePath)
		if err != nil {
			b.Fatalf("Failed to create file: %v", err)
		}

		cmd := exec.Command("go", "version")
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

		// Clean up for next iteration
		os.Remove(filePath)
	}
}

// BenchmarkRedirectToFile_Shell benchmarks using shell redirection.
// This spawns a shell which then handles the redirection.
func BenchmarkRedirectToFile_Shell(b *testing.B) {
	tmpDir := b.TempDir()
	filePath := filepath.Join(tmpDir, "output.txt")

	for i := 0; i < b.N; i++ {
		cmd := exec.Command("sh", "-c", "go version > "+filePath)
		err := cmd.Run()
		if err != nil {
			b.Fatalf("Failed to run command: %v", err)
		}

		// Clean up for next iteration
		os.Remove(filePath)
	}
}
