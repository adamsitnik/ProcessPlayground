using Microsoft.Win32.SafeHandles;

namespace Library;

internal static class LowLevelHelpers
{
    internal static bool WaitForExit(this SafeProcessHandle handle, int milliseconds)
    {
        using Interop.Kernel32.ProcessWaitHandle processWaitHandle = new(handle);
        return processWaitHandle.WaitOne(milliseconds);
    }

    internal static int GetExitCode(this SafeProcessHandle procHandle)
    {
        if (!Interop.Kernel32.GetExitCodeProcess(procHandle, out int exitCode))
        {
            throw new InvalidOperationException("Parent process should alway be able to get the exit code.");
        }
        else if (exitCode == Interop.Kernel32.HandleOptions.STILL_ACTIVE)
        {
            throw new InvalidOperationException("Process has not exited yet.");
        }

        return exitCode;
    }

    internal static int GetTimeoutInMilliseconds(TimeSpan? timeout)
        => timeout switch
        {
            null => Timeout.Infinite,
            _ when timeout.Value == Timeout.InfiniteTimeSpan => Timeout.Infinite,
            _ => (int)timeout.Value.TotalMilliseconds
        };

    internal static (SafeFileHandle input, SafeFileHandle output, SafeFileHandle error) OpenFileHandlesForRedirection(string? inputFile, string? outputFile, string? errorFile)
    {
        // All files must be opened with FileShare.Inheritable to allow the child process to inherit the handles!
        // Otherwise, the child process will not be able to use them (and Console.Out just does nothing in such case).
        SafeFileHandle inputHandle = inputFile switch
        {
            null => File.OpenNullFileHandle(),
            _ => File.OpenHandle(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Inheritable),
        };
        SafeFileHandle outputHandle = outputFile switch
        {
            null => File.OpenNullFileHandle(),
            _ => File.OpenHandle(outputFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Inheritable)
        };
        SafeFileHandle errorHandle = errorFile switch
        {
            null => File.OpenNullFileHandle(),
            // When output and error are the same file, we use the same handle!
            _ when errorFile == outputFile => outputHandle,
            _ => File.OpenHandle(errorFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read | FileShare.Write | FileShare.Inheritable),
        };

        return (inputHandle, outputHandle, errorHandle);
    }
}
