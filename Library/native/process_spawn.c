#define _GNU_SOURCE
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#include <fcntl.h>
#include <signal.h>
#include <errno.h>
#include <string.h>
#include <stdint.h>
#include <poll.h>
#include <pthread.h>

#ifdef __linux__
#include <sys/syscall.h>
#include <linux/sched.h>

// P_PIDFD is not defined in all system headers yet, so define it if missing
// This constant is defined in the Linux kernel as idtype for waitid() to accept pidfd
// See: https://man7.org/linux/man-pages/man2/waitid.2.html
#ifndef P_PIDFD
#define P_PIDFD 3
#endif
#endif

// External variable containing the current environment.
// This is a standard C global variable that points to the environment array.
// It's automatically set by the C runtime when the process starts.
// When envp parameter is NULL, we use this to pass the parent's environment to execve().
extern char **environ;

// Helper to write errno to pipe and exit (ignores write failures)
static inline void write_errno_and_exit(int pipe_fd, int err) {
    // We're about to exit anyway, so ignore write failures
    (void)write(pipe_fd, &err, sizeof(err));
    _exit(127);
}

// Helper function to create a pipe with CLOEXEC flag
static int create_cloexec_pipe(int pipefd[2]) {
#ifdef __linux__
    // On Linux, use pipe2 for atomic CLOEXEC
    return pipe2(pipefd, O_CLOEXEC);
#else
    // On other Unix systems, use pipe + fcntl
    if (pipe(pipefd) != 0) {
        return -1;
    }
    
    // Set CLOEXEC on both ends
    if (fcntl(pipefd[0], F_SETFD, FD_CLOEXEC) == -1 ||
        fcntl(pipefd[1], F_SETFD, FD_CLOEXEC) == -1) {
        int saved_errno = errno;
        close(pipefd[0]);
        close(pipefd[1]);
        errno = saved_errno;
        return -1;
    }
    
    return 0;
#endif
}

// Spawns a process and returns success/failure
// Returns 0 on success, -1 on error (errno is set)
// If out_pid is not NULL, the PID of the child process is stored there
// If out_pidfd is not NULL, the pidfd of the child process is stored there (Linux only, -1 on other platforms)
// If out_exit_pipe_fd is not NULL, the read end of exit monitoring pipe is stored there
int spawn_process(
    const char* path,
    char* const argv[],
    char* const envp[],
    int stdin_fd,
    int stdout_fd,
    int stderr_fd,
    const char* working_dir,
    int* out_pid,
    int* out_pidfd,
    int* out_exit_pipe_fd)
{
    int wait_pipe[2];
    int exit_pipe[2];
    int pidfd = -1;
    sigset_t all_signals, old_signals;
    
    // Create pipe for exec synchronization (CLOEXEC so child doesn't inherit it)
    if (create_cloexec_pipe(wait_pipe) != 0) {
        return -1;
    }
    
    // Create pipe for exit monitoring (CLOEXEC to avoid other parallel processes inheriting it)
    if (create_cloexec_pipe(exit_pipe) != 0) {
        int saved_errno = errno;
        close(wait_pipe[0]);
        close(wait_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // Block all signals before forking
    sigfillset(&all_signals);
    pthread_sigmask(SIG_SETMASK, &all_signals, &old_signals);
    
    pid_t child_pid;
    
#ifdef __linux__
    // On Linux, use clone3 to get pidfd atomically with fork
    struct clone_args args = {0};  // Zero-initialize
    args.flags = CLONE_VFORK | CLONE_PIDFD;
    args.pidfd = (uint64_t)(uintptr_t)&pidfd;
    args.exit_signal = SIGCHLD;
    
    long clone_result = syscall(SYS_clone3, &args, sizeof(args));
    
    if (clone_result == -1) {
        // Fork failed
        int saved_errno = errno;
        pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
        close(wait_pipe[0]);
        close(wait_pipe[1]);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    child_pid = (pid_t)clone_result;
    
    if (clone_result == 0) {
#else
    // On non-Linux Unix, use vfork
    child_pid = vfork();
    
    if (child_pid == -1) {
        // Fork failed
        int saved_errno = errno;
        pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
        close(wait_pipe[0]);
        close(wait_pipe[1]);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    if (child_pid == 0) {
#endif
        // ========== CHILD PROCESS ==========
        
        // Restore signal mask immediately
        pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
        
        // Reset all signal handlers to default
        struct sigaction sa_default;
        struct sigaction sa_old;
        memset(&sa_default, 0, sizeof(sa_default));
        sa_default.sa_handler = SIG_DFL;
        
        for (int sig = 1; sig < NSIG; sig++) {
            if (sig == SIGKILL || sig == SIGSTOP) continue;

            if (!sigaction(sig, NULL, &sa_old))
            {
                void (*oldhandler)(int) = sa_old.sa_handler;
                if (oldhandler != SIG_IGN && oldhandler != SIG_DFL)
                {
                    // It has a custom handler, put the default handler back.
                    // We check first to preserve flags on default handlers.
                    sigaction(sig, &sa_default, NULL);
                }
            }
        }
        
        // Close read end of wait pipe (we only write)
        close(wait_pipe[0]);
        
        // Duplicate exit pipe write end to fd 3 (so it survives execve)
        // We use fd 3 as it's typically unused by standard streams
        if (dup2(exit_pipe[1], 3) == -1) {
            write_errno_and_exit(wait_pipe[1], errno);
        }
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        
        // Redirect stdin/stdout/stderr
        if (stdin_fd != 0) {
            if (dup2(stdin_fd, 0) == -1) {
                write_errno_and_exit(wait_pipe[1], errno);
            }
        }
        
        if (stdout_fd != 1) {
            if (dup2(stdout_fd, 1) == -1) {
                write_errno_and_exit(wait_pipe[1], errno);
            }
        }
        
        if (stderr_fd != 2) {
            if (dup2(stderr_fd, 2) == -1) {
                write_errno_and_exit(wait_pipe[1], errno);
            }
        }
        
        // Change working directory if specified
        if (working_dir != NULL) {
            if (chdir(working_dir) == -1) {
                write_errno_and_exit(wait_pipe[1], errno);
            }
        }
        
        // Execute the program
        // If envp is NULL, use the current environment (environ)
        char* const* env = (envp != NULL) ? envp : environ;
        execve(path, argv, env);
        
        // If we get here, execve failed
        write_errno_and_exit(wait_pipe[1], errno);
    }
    
    // ========== PARENT PROCESS ==========
    
    // Restore signal mask
    pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
    
    // Close write end of wait pipe
    close(wait_pipe[1]);
    
    // Close write end of exit pipe (child owns it)
    close(exit_pipe[1]);
    
    // Wait for child to exec or fail
    int child_errno = 0;
    ssize_t bytes_read = read(wait_pipe[0], &child_errno, sizeof(child_errno));
    close(wait_pipe[0]);
    
    if (bytes_read == sizeof(child_errno)) {
        // Child failed to exec - reap it and close exit pipe
#ifdef __linux__
        siginfo_t info;
        waitid(P_PIDFD, pidfd, &info, WEXITED);
        close(pidfd);
#else
        int status;
        waitpid(child_pid, &status, 0);
#endif
        close(exit_pipe[0]);
        errno = child_errno;
        return -1;
    }
    
    // Success - return PID, pidfd, and exit pipe fd if requested
    if (out_pid != NULL) {
        *out_pid = child_pid;
    }
    if (out_pidfd != NULL) {
#ifdef __linux__
        *out_pidfd = pidfd;
#else
        *out_pidfd = -1;  // pidfd not available on non-Linux platforms
#endif
    }
    if (out_exit_pipe_fd != NULL) {
        *out_exit_pipe_fd = exit_pipe[0];
    }
    return 0;
}

#ifdef __linux__
// No helper function needed on Linux because we use siginfo_t which gives us the exit code directly
#else
// Helper function to extract exit code from status returned by waitpid
static int get_exit_code_from_status(int status) {
    // Check if the process exited normally
    if (WIFEXITED(status)) {
        // Process exited normally, return exit code
        return WEXITSTATUS(status);
    } else {
        // Process was terminated by a signal
        return -1;
    }
}
#endif

#ifdef __linux__
// Tries to get the exit code of a process without blocking (Linux with pidfd)
// Returns 1 if exit code was retrieved, 0 if process is still running, -1 on error (errno is set)
int try_get_exit_code_native(int pidfd, int* out_exit_code) {
    siginfo_t siginfo;
    memset(&siginfo, 0, sizeof(siginfo));
    
    int result = waitid(P_PIDFD, pidfd, &siginfo, WEXITED | WNOHANG);
    
    if (result == 0) {
        // waitid returns 0 when the process has exited or is still running.
        // Check if siginfo was filled (process actually exited)
        // si_signo will be non-zero (typically SIGCHLD) if process exited
        // si_signo will be 0 if process is still running
        if (siginfo.si_signo != 0) {
            if (out_exit_code != NULL) {
                *out_exit_code = siginfo.si_status;
            }
            return 1;  // Exit code retrieved
        }
        return 0;  // Process still running
    }
    
    // Error occurred
    return -1;
}

// Waits for a process to exit without timeout (Linux with pidfd)
// Returns exit code on success, -1 on error (errno is set)
int wait_for_exit_no_timeout_native(int pidfd) {
    siginfo_t siginfo;
    
    while (1) {
        memset(&siginfo, 0, sizeof(siginfo));
        int result = waitid(P_PIDFD, pidfd, &siginfo, WEXITED);
        if (result == 0) {
            return siginfo.si_status;
        } else {
            if (errno != EINTR) {
                return -1;
            }
            // EINTR - interrupted by signal, retry
        }
    }
}

// Waits for a process to exit with timeout (Linux with pidfd)
// Returns exit code on success, -1 on error (errno is set)
// If timeout occurs, returns exit code after killing the process
// Parameters:
//   pidfd: process file descriptor
//   timeout_ms: timeout in milliseconds
//   kill_on_timeout: if non-zero, kill the process on timeout
//   out_timed_out: if not NULL, set to 1 if timeout occurred, 0 otherwise
int wait_for_exit_native(int pidfd, int timeout_ms, int kill_on_timeout, int* out_timed_out) {
    struct pollfd pollfd;
    pollfd.fd = pidfd;
    pollfd.events = POLLIN;
    pollfd.revents = 0;
    
    while (1) {
        int poll_result = poll(&pollfd, 1, timeout_ms);
        
        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;  // Interrupted by signal, retry
            }
            return -1;  // Error
        } else if (poll_result == 0) {
            // Timeout
            if (out_timed_out != NULL) {
                *out_timed_out = 1;
            }
            
            if (kill_on_timeout) {
                // Kill the process using pidfd_send_signal
                // Ignore errors - process may have already exited
                syscall(SYS_pidfd_send_signal, pidfd, SIGKILL, NULL, 0);
            }
            
            // Wait for the process to actually exit
            return wait_for_exit_no_timeout_native(pidfd);
        } else {
            // Process exited
            if (out_timed_out != NULL) {
                *out_timed_out = 0;
            }
            
            // Retrieve exit code
            siginfo_t siginfo;
            while (1) {
                memset(&siginfo, 0, sizeof(siginfo));
                int result = waitid(P_PIDFD, pidfd, &siginfo, WEXITED | WNOHANG);
                if (result == 0) {
                    return siginfo.si_status;
                } else {
                    if (errno != EINTR) {
                        return -1;
                    }
                }
            }
        }
    }
}

// Reads the exit code after the process has already exited (Linux with pidfd)
// This is called when we know the process has exited (e.g., exit pipe was closed)
// Returns exit code on success, -1 on error (errno is set)
int get_exit_code_after_exit_native(int pidfd) {
    siginfo_t siginfo;
    
    while (1) {
        memset(&siginfo, 0, sizeof(siginfo));
        int result = waitid(P_PIDFD, pidfd, &siginfo, WEXITED | WNOHANG);
        if (result == 0) {
            return siginfo.si_status;
        } else {
            if (errno != EINTR) {
                return -1;
            }
        }
    }
}

#else  // Non-Linux Unix

// Tries to get the exit code of a process without blocking (non-Linux Unix)
// Returns 1 if exit code was retrieved, 0 if process is still running, -1 on error (errno is set)
int try_get_exit_code_native(int pid, int* out_exit_code) {
    int status = 0;
    int result = waitpid(pid, &status, WNOHANG);
    
    if (result == pid) {
        // Process has exited
        if (out_exit_code != NULL) {
            *out_exit_code = get_exit_code_from_status(status);
        }
        return 1;  // Exit code retrieved
    } else if (result == 0) {
        // Process still running
        return 0;
    }
    
    // Error occurred
    return -1;
}

// Waits for a process to exit without timeout (non-Linux Unix)
// Returns exit code on success, -1 on error (errno is set)
int wait_for_exit_no_timeout_native(int pid) {
    int status = 0;
    
    while (1) {
        int result = waitpid(pid, &status, 0);
        if (result == pid) {
            return get_exit_code_from_status(status);
        } else if (result == -1) {
            if (errno != EINTR) {
                return -1;
            }
            // EINTR - interrupted by signal, retry
        }
    }
}

// Waits for a process to exit with timeout (non-Linux Unix)
// Returns exit code on success, -1 on error (errno is set)
// If timeout occurs, returns exit code after killing the process
// Parameters:
//   pid: process ID
//   exit_pipe_fd: file descriptor for exit monitoring pipe
//   timeout_ms: timeout in milliseconds
//   kill_on_timeout: if non-zero, kill the process on timeout
//   out_timed_out: if not NULL, set to 1 if timeout occurred, 0 otherwise
int wait_for_exit_native(int pid, int exit_pipe_fd, int timeout_ms, int kill_on_timeout, int* out_timed_out) {
    struct pollfd pollfd;
    pollfd.fd = exit_pipe_fd;
    pollfd.events = POLLIN;
    pollfd.revents = 0;
    
    while (1) {
        int poll_result = poll(&pollfd, 1, timeout_ms);
        
        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;  // Interrupted by signal, retry
            }
            return -1;  // Error
        } else if (poll_result == 0) {
            // Timeout
            if (out_timed_out != NULL) {
                *out_timed_out = 1;
            }
            
            if (kill_on_timeout) {
                // Kill the process
                // Ignore errors - process may have already exited
                kill(pid, SIGKILL);
            }
            
            // Wait for the process to actually exit and return its exit code
            return wait_for_exit_no_timeout_native(pid);
        } else {
            // Exit pipe became readable - process has exited
            if (out_timed_out != NULL) {
                *out_timed_out = 0;
            }
            return wait_for_exit_no_timeout_native(pid);
        }
    }
}

// Reads the exit code after the process has already exited (non-Linux Unix)
// This is called when we know the process has exited (e.g., exit pipe was closed)
// Returns exit code on success, -1 on error (errno is set)
int get_exit_code_after_exit_native(int pid) {
    return wait_for_exit_no_timeout_native(pid);
}

#endif