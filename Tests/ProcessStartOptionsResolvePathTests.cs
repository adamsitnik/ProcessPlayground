using System;
using System.IO;
using System.TBA;

namespace Tests;

public class ProcessStartOptionsResolvePathTests
{
    [Fact]
    public static void ResolvePath_ThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => ProcessStartOptions.ResolvePath(null!));
    }

    [Fact]
    public static void ResolvePath_ThrowsOnEmpty()
    {
        Assert.Throws<ArgumentException>(() => ProcessStartOptions.ResolvePath(string.Empty));
    }

    [Fact]
    public static void ResolvePath_ThrowsFileNotFoundException_WhenFileDoesNotExist()
    {
        string nonExistentFile = Guid.NewGuid().ToString() + ".exe";
        Assert.Throws<FileNotFoundException>(() => ProcessStartOptions.ResolvePath(nonExistentFile));
    }

    [Fact]
    public static void ResolvePath_ReturnsAbsolutePath_WhenGivenAbsolutePath()
    {
        // Create a temporary file
        string tempFile = Path.GetTempFileName();
        try
        {
            var options = ProcessStartOptions.ResolvePath(tempFile);
            Assert.NotNull(options);
            Assert.Equal(tempFile, options.FileName);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public static void ResolvePath_ReturnsAbsolutePath_EvenIfFileDoesNotExist_WhenGivenAbsolutePath()
    {
        // For rooted paths, the method should return the path even if the file doesn't exist
        string nonExistentAbsolutePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".exe");
        
        var options = ProcessStartOptions.ResolvePath(nonExistentAbsolutePath);
        Assert.NotNull(options);
        Assert.Equal(nonExistentAbsolutePath, options.FileName);
    }

    [Fact]
    public static void ResolvePath_FindsFileInCurrentDirectory()
    {
        // Create a temporary file in current directory
        string fileName = Guid.NewGuid().ToString() + ".tmp";
        string fullPath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        
        try
        {
            File.WriteAllText(fullPath, "test");
            
            var options = ProcessStartOptions.ResolvePath(fileName);
            Assert.NotNull(options);
            Assert.Equal(fullPath, options.FileName);
        }
        finally
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }

    [Fact]
    public static void ResolvePath_FindsExecutableInPath()
    {
        // Test with a common executable that should be in PATH
        string executable = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
        
        var options = ProcessStartOptions.ResolvePath(executable);
        Assert.NotNull(options);
        Assert.NotNull(options.FileName);
        Assert.True(File.Exists(options.FileName), $"Expected to find {executable} but got {options.FileName}");
        Assert.True(Path.IsPathRooted(options.FileName), "Expected an absolute path");
    }

#if WINDOWS
    [Theory]
#endif
    [InlineData("cmd.exe")]
    [InlineData("notepad.exe")]
    public static void ResolvePath_FindsCommonWindowsExecutables(string executable)
    {
        var options = ProcessStartOptions.ResolvePath(executable);
        Assert.NotNull(options);
        Assert.NotNull(options.FileName);
        Assert.True(File.Exists(options.FileName), $"Expected to find {executable}");
    }

#if !WINDOWS
    [Theory]
#endif
    [InlineData("sh")]
    [InlineData("ls")]
    [InlineData("cat")]
    public static void ResolvePath_FindsCommonUnixExecutables(string executable)
    {
        var options = ProcessStartOptions.ResolvePath(executable);
        Assert.NotNull(options);
        Assert.NotNull(options.FileName);
        Assert.True(File.Exists(options.FileName), $"Expected to find {executable}");
    }

    [Fact]
    public static void ResolvePath_FindsFileInExecutableDirectory()
    {
        // Note: The "executable directory" refers to the directory where the current process
        // executable is located (e.g., dotnet.exe or the test host), not the test assembly.
        // This test verifies that files next to the process executable can be found.
        
        string? execPath = GetExecutablePath();
        if (execPath == null)
        {
            // Skip if we can't determine executable path
            return;
        }

        string? execDir = Path.GetDirectoryName(execPath);
        if (execDir == null || !Directory.Exists(execDir))
        {
            return;
        }

        // Look for an actual file that exists in the executable directory
        // On Unix, we're looking for the dotnet executable itself
        // On Windows, we might be looking for the test host
        string execName = Path.GetFileName(execPath);
        
        // Save current directory and change it to ensure we're not finding via current dir
        string originalDir = Directory.GetCurrentDirectory();
        try
        {
            // Change to a different directory
            Directory.SetCurrentDirectory(Path.GetTempPath());
            
            // Try to resolve just the executable name - it should find it in the executable directory
            var options = ProcessStartOptions.ResolvePath(execName);
            Assert.NotNull(options);
            Assert.True(File.Exists(options.FileName), $"Expected to find {execName}");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDir);
        }
    }

    private static string? GetExecutablePath()
    {
        return System.Environment.ProcessPath;
    }
}
