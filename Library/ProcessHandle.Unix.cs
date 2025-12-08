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
    private static extern unsafe int posix_spawn(
        int* pid,
        byte* path,
        void* file_actions,  // posix_spawn_file_actions_t*
        void* attrp,         // posix_spawnattr_t*
        byte** argv,
        byte** envp);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_init(void* file_actions);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_destroy(void* file_actions);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_adddup2(void* file_actions, int fildes, int newfildes);
    
    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int posix_spawn_file_actions_addchdir_np(void* file_actions, byte* path);
    
    private const int WNOHANG = 1;
    private const int SIGKILL = 9;
    private const int EINTR = 4;
    private const int ECHILD = 10;
    
    private static unsafe SafeProcessHandle StartCore(ProcessStartOptions options, SafeFileHandle inputHandle, SafeFileHandle outputHandle, SafeFileHandle errorHandle)
    {
        // Resolve executable path first
        string? resolvedPath = ResolvePath(options.FileName);
        if (string.IsNullOrEmpty(resolvedPath))
        {
            throw new Win32Exception(2, $"Cannot find executable: {options.FileName}");
        }
        
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

        // Allocate all native memory
        IntPtr[] argvHandles = new IntPtr[argList.Count];
        IntPtr[] envpHandles = new IntPtr[envList.Count];
        IntPtr filePathHandle = IntPtr.Zero;
        IntPtr cwdHandle = IntPtr.Zero;
        
        byte*[] argvPtrs = new byte*[argList.Count + 1];
        byte*[] envpPtrs = new byte*[envList.Count + 1];
        
        // Allocate file_actions on the stack (typical size is 80 bytes, use 128 to be safe)
        byte* fileActionsBuffer = stackalloc byte[128];
        void* fileActions = fileActionsBuffer;
        bool fileActionsInitialized = false;
        
        try
        {
            // Marshal file path
            filePathHandle = Marshal.StringToHGlobalAnsi(resolvedPath);
            byte* filePathPtr = (byte*)filePathHandle;
            
            // Marshal working directory if specified
            byte* cwdPtr = null;
            if (options.WorkingDirectory != null)
            {
                cwdHandle = Marshal.StringToHGlobalAnsi(options.WorkingDirectory.FullName);
                cwdPtr = (byte*)cwdHandle;
            }
            
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
            
            // Initialize file actions
            if (posix_spawn_file_actions_init(fileActions) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "posix_spawn_file_actions_init failed");
            }
            fileActionsInitialized = true;
            
            // Add file descriptor redirections
            if (stdinFd != 0)
            {
                posix_spawn_file_actions_adddup2(fileActions, stdinFd, 0);
            }
            if (stdoutFd != 1)
            {
                posix_spawn_file_actions_adddup2(fileActions, stdoutFd, 1);
            }
            if (stderrFd != 2)
            {
                posix_spawn_file_actions_adddup2(fileActions, stderrFd, 2);
            }
            
            // Add working directory change if specified (glibc 2.29+)
            if (cwdPtr != null)
            {
                // Note: addchdir_np is a non-portable extension, may not be available
                // We'll try it, but if it fails, we'll proceed without it
                posix_spawn_file_actions_addchdir_np(fileActions, cwdPtr);
            }
            
            // Spawn the process
            int pid = 0;
            fixed (byte** argv = argvPtrs)
            fixed (byte** envp = envpPtrs)
            {
                int result = posix_spawn(&pid, filePathPtr, fileActions, null, argv, envp);
                if (result != 0)
                {
                    throw new Win32Exception(result, $"posix_spawn failed for {resolvedPath}");
                }
            }
            
            // If working directory couldn't be set via file actions, we'll have to accept it
            // The child process will inherit the parent's working directory
            
            return new SafeProcessHandle((IntPtr)pid, ownsHandle: true);
        }
        finally
        {
            // Clean up file actions
            if (fileActionsInitialized)
            {
                posix_spawn_file_actions_destroy(fileActions);
            }
            
            // Free marshaled strings
            if (filePathHandle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(filePathHandle);
            }
            if (cwdHandle != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(cwdHandle);
            }
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
