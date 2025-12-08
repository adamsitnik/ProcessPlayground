using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static Tmds.Linux.LibC;

namespace Library;

public static partial class ProcessHandle
{
    // P/Invoke declarations for functions not in Tmds.LibC
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int waitpid(int pid, int* status, int options);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int chdir(byte* path);
    
    private const int WNOHANG = 1;
    private const int SIGKILL = 9;
    private const int EINTR = 4;
    private const int ECHILD = 10;
    
    private static unsafe SafeProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        // Prepare arguments array (argv)
        List<string> argList = new() { options.FileName };
        argList.AddRange(options.Arguments);
        
        // Prepare environment array (envp)
        List<string> envList = new();
        if (options.Environment.Count > 0)
        {
            foreach (var kvp in options.Environment)
            {
                if (kvp.Value != null)
                {
                    envList.Add($"{kvp.Key}={kvp.Value}");
                }
            }
        }
        else
        {
            // Use current environment
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                envList.Add($"{entry.Key}={entry.Value}");
            }
        }

        // Convert to native arrays
        byte*[] argvPtrs = new byte*[argList.Count + 1];
        byte*[] envpPtrs = new byte*[envList.Count + 1];
        
        IntPtr[] argvHandles = new IntPtr[argList.Count];
        IntPtr[] envpHandles = new IntPtr[envList.Count];
        
        try
        {
            // Marshal argv
            for (int i = 0; i < argList.Count; i++)
            {
                argvHandles[i] = Marshal.StringToHGlobalAnsi(argList[i]);
                argvPtrs[i] = (byte*)argvHandles[i];
            }
            argvPtrs[argList.Count] = null;
            
            // Marshal envp
            for (int i = 0; i < envList.Count; i++)
            {
                envpHandles[i] = Marshal.StringToHGlobalAnsi(envList[i]);
                envpPtrs[i] = (byte*)envpHandles[i];
            }
            envpPtrs[envList.Count] = null;
            
            // Get file descriptors
            int stdinFd = (int)inputHandle.DangerousGetHandle();
            int stdoutFd = (int)outputHandle.DangerousGetHandle();
            int stderrFd = (int)errorHandle.DangerousGetHandle();
            
            // Resolve executable path
            string? resolvedPath = ResolvePath(options.FileName);
            if (string.IsNullOrEmpty(resolvedPath))
            {
                throw new Win32Exception(2, $"Cannot find executable: {options.FileName}");
            }
            
            byte* filePathPtr = null;
            IntPtr filePathHandle = IntPtr.Zero;
            try
            {
                filePathHandle = Marshal.StringToHGlobalAnsi(resolvedPath);
                filePathPtr = (byte*)filePathHandle;
                
                // Fork the process
                int pid = fork();
                
                if (pid == -1)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "fork() failed");
                }
                else if (pid == 0)
                {
                    // Child process
                    try
                    {
                        // Change working directory if specified
                        if (options.WorkingDirectory != null)
                        {
                            IntPtr cwdHandle = Marshal.StringToHGlobalAnsi(options.WorkingDirectory.FullName);
                            try
                            {
                                if (chdir((byte*)cwdHandle) != 0)
                                {
                                    _exit(127);
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(cwdHandle);
                            }
                        }
                        
                        // Redirect file descriptors
                        if (stdinFd != 0)
                        {
                            dup2(stdinFd, 0);
                            close(stdinFd);
                        }
                        if (stdoutFd != 1)
                        {
                            dup2(stdoutFd, 1);
                            close(stdoutFd);
                        }
                        if (stderrFd != 2)
                        {
                            dup2(stderrFd, 2);
                            close(stderrFd);
                        }
                        
                        // Close all other file descriptors (best effort)
                        for (int fd = 3; fd < 256; fd++)
                        {
                            close(fd);
                        }
                        
                        // Execute the new program
                        fixed (byte** argv = argvPtrs)
                        fixed (byte** envp = envpPtrs)
                        {
                            execve(filePathPtr, argv, envp);
                        }
                        
                        // If execve returns, it failed
                        _exit(127);
                    }
                    catch
                    {
                        _exit(127);
                    }
                    
                    // This should never be reached, but satisfies the compiler
                    throw new InvalidOperationException("Unreachable code in child process");
                }
                else
                {
                    // Parent process
                    return new SafeProcessHandle((IntPtr)pid, ownsHandle: true);
                }
            }
            finally
            {
                if (filePathHandle != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(filePathHandle);
                }
            }
        }
        finally
        {
            // Free marshaled strings
            foreach (var handle in argvHandles)
            {
                if (handle != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(handle);
                }
            }
            foreach (var handle in envpHandles)
            {
                if (handle != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(handle);
                }
            }
        }
    }

    private static int GetProcessIdCore(SafeProcessHandle processHandle)
        => (int)processHandle.DangerousGetHandle();

    private static unsafe int WaitForExitCore(SafeProcessHandle processHandle, int milliseconds)
    {
        int pid = (int)processHandle.DangerousGetHandle();
        int status = 0;
        
        if (milliseconds == Timeout.Infinite)
        {
            // Wait indefinitely
            while (true)
            {
                int result = waitpid(pid, &status, 0);
                if (result == pid)
                {
                    return GetExitCodeFromStatus(status);
                }
                else if (result == -1)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR) // interrupted system call, retry
                    {
                        continue;
                    }
                    throw new Win32Exception(errno, "waitpid() failed");
                }
            }
        }
        else
        {
            // Wait with timeout using polling
            long startTime = Environment.TickCount64;
            long endTime = startTime + milliseconds;
            
            while (true)
            {
                // Non-blocking wait
                int result = waitpid(pid, &status, WNOHANG);
                if (result == pid)
                {
                    return GetExitCodeFromStatus(status);
                }
                else if (result == -1)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == EINTR)
                    {
                        continue;
                    }
                    throw new Win32Exception(errno, "waitpid() failed");
                }
                else if (result == 0)
                {
                    // Process still running
                    long now = Environment.TickCount64;
                    if (now >= endTime)
                    {
                        // Timeout - terminate the process
                        kill(pid, SIGKILL);
                        
                        // Wait for it to actually exit
                        while (true)
                        {
                            result = waitpid(pid, &status, 0);
                            if (result == pid)
                            {
                                return GetExitCodeFromStatus(status);
                            }
                            else if (result == -1)
                            {
                                int errno = Marshal.GetLastPInvokeError();
                                if (errno != EINTR)
                                {
                                    throw new Win32Exception(errno, "waitpid() failed after timeout");
                                }
                            }
                        }
                    }
                    
                    // Sleep a bit before polling again
                    Thread.Sleep(10);
                }
            }
        }
    }

    private static async Task<int> WaitForExitAsyncCore(SafeProcessHandle processHandle, CancellationToken cancellationToken)
    {
        int pid = (int)processHandle.DangerousGetHandle();
        
        // Register cancellation to terminate the process
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                kill(pid, SIGKILL);
            }
            catch
            {
                // Ignore errors during cancellation
            }
        });
        
        // Poll for process exit in a background task
        return await Task.Run(() =>
        {
            unsafe
            {
                int status = 0;
                
                while (!cancellationToken.IsCancellationRequested)
                {
                    // Non-blocking wait
                    int result = waitpid(pid, &status, WNOHANG);
                    if (result == pid)
                    {
                        return GetExitCodeFromStatus(status);
                    }
                    else if (result == -1)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        if (errno == EINTR)
                        {
                            continue;
                        }
                        else if (errno == ECHILD) // no child process
                        {
                            // Process already exited or doesn't exist
                            return -1;
                        }
                        throw new Win32Exception(errno, "waitpid() failed");
                    }
                    else if (result == 0)
                    {
                        // Process still running, wait a bit
                        Thread.Sleep(10);
                    }
                }
                
                // If we get here, we were cancelled
                throw new OperationCanceledException(cancellationToken);
            }
        }, cancellationToken).ConfigureAwait(false);
    }
    
    private static int GetExitCodeFromStatus(int status)
    {
        // Check if the process exited normally
        if ((status & 0x7F) == 0)
        {
            // WIFEXITED - process exited normally
            return (status & 0xFF00) >> 8; // WEXITSTATUS
        }
        else
        {
            // Process was terminated by a signal
            return -1;
        }
    }
    
    private static string? ResolvePath(string fileName)
    {
        // If it's an absolute path, use it directly
        if (Path.IsPathRooted(fileName))
        {
            return System.IO.File.Exists(fileName) ? fileName : null;
        }
        
        // If it contains a path separator, treat it as relative
        if (fileName.Contains('/'))
        {
            string fullPath = Path.GetFullPath(fileName);
            return System.IO.File.Exists(fullPath) ? fullPath : null;
        }
        
        // Search in PATH
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }
        
        foreach (string dir in pathEnv.Split(':'))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }
            
            string fullPath = Path.Combine(dir, fileName);
            if (System.IO.File.Exists(fullPath))
            {
                // Check if it's executable
                if (IsExecutable(fullPath))
                {
                    return fullPath;
                }
            }
        }
        
        return null;
    }
    
    private static unsafe bool IsExecutable(string path)
    {
        IntPtr pathHandle = Marshal.StringToHGlobalAnsi(path);
        try
        {
            // Check for execute permission (X_OK = 1)
            return access((byte*)pathHandle, 1) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(pathHandle);
        }
    }
}
