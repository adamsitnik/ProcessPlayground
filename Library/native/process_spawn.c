#define _GNU_SOURCE
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#include <fcntl.h>
#include <signal.h>
#include <errno.h>
#include <string.h>
#include <stdint.h>

#ifdef __linux__
#include <sys/syscall.h>
#include <linux/sched.h>
#include <sys/prctl.h>

// Define syscall number if not available in headers
#ifndef __NR_pidfd_send_signal
#define __NR_pidfd_send_signal 424
#endif

// close_range syscall and flags
#ifndef __NR_close_range
#define __NR_close_range 436
#endif

#ifndef CLOSE_RANGE_CLOEXEC
#define CLOSE_RANGE_CLOEXEC (1U << 2)
#endif
#endif

#ifdef __APPLE__
#include <spawn.h>

// Define the flag if not available (should be available on macOS 10.9+)
#ifndef POSIX_SPAWN_CLOEXEC_DEFAULT
#define POSIX_SPAWN_CLOEXEC_DEFAULT 0x4000
#endif

// Declare the non-portable chdir function if not available in headers
// This is available on macOS 10.15+ but may not be in older SDK headers
#ifndef __has_include
    extern int posix_spawn_file_actions_addchdir_np(posix_spawn_file_actions_t *, const char *);
#elif !__has_include(<sys/_types/_posix_spawn_file_actions_addchdir_np.h>)
    extern int posix_spawn_file_actions_addchdir_np(posix_spawn_file_actions_t *, const char *);
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
// If kill_on_parent_death is non-zero, the child process will be killed when the parent dies
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
    int* out_exit_pipe_fd,
    int kill_on_parent_death)
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
    
#ifdef __APPLE__
    // ========== macOS: Use posix_spawn with POSIX_SPAWN_CLOEXEC_DEFAULT ==========
    
    posix_spawn_file_actions_t file_actions;
    posix_spawnattr_t attr;
    int spawn_errno = 0;
    
    // Initialize spawn attributes
    if (posix_spawnattr_init(&attr) != 0) {
        int saved_errno = errno;
        pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
        close(wait_pipe[0]);
        close(wait_pipe[1]);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // Initialize file actions
    if (posix_spawn_file_actions_init(&file_actions) != 0) {
        int saved_errno = errno;
        posix_spawnattr_destroy(&attr);
        pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
        close(wait_pipe[0]);
        close(wait_pipe[1]);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // Set POSIX_SPAWN_CLOEXEC_DEFAULT flag to close all FDs except those we explicitly inherit
    // Also set POSIX_SPAWN_SETSIGMASK to restore the signal mask
    short flags = POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGMASK;
    if (posix_spawnattr_setflags(&attr, flags) != 0) {
        spawn_errno = errno;
        goto spawn_cleanup;
    }
    
    // Restore signal mask in child
    if (posix_spawnattr_setsigmask(&attr, &old_signals) != 0) {
        spawn_errno = errno;
        goto spawn_cleanup;
    }
    
    // Set up file actions to redirect stdin/stdout/stderr
    // Note: We need to dup2 the handles before closing the write end of wait_pipe
    if (stdin_fd != 0) {
        if (posix_spawn_file_actions_adddup2(&file_actions, stdin_fd, 0) != 0) {
            spawn_errno = errno;
            goto spawn_cleanup;
        }
    }
    
    if (stdout_fd != 1) {
        if (posix_spawn_file_actions_adddup2(&file_actions, stdout_fd, 1) != 0) {
            spawn_errno = errno;
            goto spawn_cleanup;
        }
    }
    
    if (stderr_fd != 2) {
        if (posix_spawn_file_actions_adddup2(&file_actions, stderr_fd, 2) != 0) {
            spawn_errno = errno;
            goto spawn_cleanup;
        }
    }
    
    // Duplicate exit pipe write end to fd 3
    if (posix_spawn_file_actions_adddup2(&file_actions, exit_pipe[1], 3) != 0) {
        spawn_errno = errno;
        goto spawn_cleanup;
    }
    
    // Close the read ends of both pipes in the child (they have CLOEXEC but we're using posix_spawn)
    if (posix_spawn_file_actions_addclose(&file_actions, wait_pipe[0]) != 0) {
        spawn_errno = errno;
        goto spawn_cleanup;
    }
    
    if (posix_spawn_file_actions_addclose(&file_actions, exit_pipe[0]) != 0) {
        spawn_errno = errno;
        goto spawn_cleanup;
    }
    
    // After dup2 operations, close the original FDs if they're not stdin/stdout/stderr
    // This ensures we don't leak file descriptors
    if (exit_pipe[1] != 3) {
        if (posix_spawn_file_actions_addclose(&file_actions, exit_pipe[1]) != 0) {
            spawn_errno = errno;
            goto spawn_cleanup;
        }
    }
    
    if (stdin_fd != 0 && stdin_fd != 1 && stdin_fd != 2 && stdin_fd != 3) {
        posix_spawn_file_actions_addclose(&file_actions, stdin_fd);
    }
    
    if (stdout_fd != 0 && stdout_fd != 1 && stdout_fd != 2 && stdout_fd != 3) {
        posix_spawn_file_actions_addclose(&file_actions, stdout_fd);
    }
    
    if (stderr_fd != 0 && stderr_fd != 1 && stderr_fd != 2 && stderr_fd != 3) {
        posix_spawn_file_actions_addclose(&file_actions, stderr_fd);
    }
    
    // Change working directory if specified
    if (working_dir != NULL) {
        if (posix_spawn_file_actions_addchdir_np(&file_actions, working_dir) != 0) {
            spawn_errno = errno;
            goto spawn_cleanup;
        }
    }
    
    // Use the current environment if envp is NULL
    char* const* env = (envp != NULL) ? envp : environ;
    
    // Spawn the process
    int result = posix_spawn(&child_pid, path, &file_actions, &attr, argv, env);
    
spawn_cleanup:
    // Clean up spawn structures
    posix_spawn_file_actions_destroy(&file_actions);
    posix_spawnattr_destroy(&attr);
    
    // Restore signal mask
    pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
    
    if (result != 0 || spawn_errno != 0) {
        // Spawn failed
        close(wait_pipe[0]);
        close(wait_pipe[1]);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = (spawn_errno != 0) ? spawn_errno : result;
        return -1;
    }
    
    // Close write end of wait pipe (not needed for posix_spawn)
    close(wait_pipe[1]);
    
    // Close write end of exit pipe (child owns it)
    close(exit_pipe[1]);
    
    // With posix_spawn, we don't have a wait pipe mechanism to detect exec failures
    // The process is already running at this point
    // We need to check if it exited immediately
    // Close the read end of wait pipe as it's not used
    close(wait_pipe[0]);
    
    // Success - return PID and exit pipe fd if requested
    if (out_pid != NULL) {
        *out_pid = child_pid;
    }
    if (out_pidfd != NULL) {
        *out_pidfd = -1;  // pidfd not available on macOS
    }
    if (out_exit_pipe_fd != NULL) {
        *out_exit_pipe_fd = exit_pipe[0];
    }
    return 0;
    
#elif defined(__linux__)
    // ========== Linux: Use clone3 with CLONE_PIDFD ==========
    
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
    // ========== Other Unix: Use vfork ==========
    
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
#if !defined(__APPLE__)
        // ========== CHILD PROCESS (Linux and other Unix, not macOS) ==========
        // Note: On macOS, we use posix_spawn so this code path is not executed
        
        // Restore signal mask immediately
        pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
        
        // If kill_on_parent_death is enabled, set up parent death signal
        if (kill_on_parent_death) {
#ifdef __linux__
            // On Linux, use prctl to set up parent death signal
            if (prctl(PR_SET_PDEATHSIG, SIGTERM) == -1) {
                write_errno_and_exit(wait_pipe[1], errno);
            }
            
            // Close the race: parent may have already died before prctl() ran.
            // Note: This checks if we've been reparented to init (PID 1).
            // In containers or systems with different init systems, this may not
            // work perfectly, but it's the standard approach for this functionality.
            if (getppid() == 1) {
                // Parent already gone; we've been reparented to init.
                // Exit immediately to honor the kill-on-parent-death contract.
                _exit(0);
            }
#else
            // On non-Linux Unix systems, prctl is not available.
            // We would need a different mechanism (like polling or signals),
            // but for now we'll skip it as it's not straightforward to implement
            // without platform-specific code.
            // This is a limitation of the Unix implementation on non-Linux systems.
#endif
        }
        
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
        
#ifdef __linux__
        // On Linux, use close_range to mark all FDs from 4 onwards as CLOEXEC
        // This prevents the child from inheriting unwanted file descriptors
        // FDs 0-2 are stdin/stdout/stderr, fd 3 is our exit pipe
        // We use CLOSE_RANGE_CLOEXEC to set the flag without closing the FDs
        // This must be called AFTER the dup2 calls above, so that if stdin_fd/stdout_fd/stderr_fd
        // are >= 4, they don't get CLOEXEC set before being duplicated to 0/1/2
        syscall(__NR_close_range, 4, ~0U, CLOSE_RANGE_CLOEXEC);
        // Ignore errors - if close_range is not supported, we continue anyway
#endif
        
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
    
#endif  // !defined(__APPLE__)
    
#if !defined(__APPLE__)
    // ========== PARENT PROCESS (Linux and other Unix, not macOS) ==========
    // Note: On macOS, the parent process code is above in the posix_spawn section
    
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
#endif  // !defined(__APPLE__)
}

// Map managed PosixSignal enum values to native signal numbers
// The managed enum uses negative values: SIGHUP=-1, SIGINT=-2, etc.
// Special case: SIGKILL=9 is passed as a positive value
// This function converts them to the actual platform-specific signal numbers
static int map_managed_signal_to_native(int managed_signal) {
    // Otherwise, map from managed enum values
    switch (managed_signal) {
        case 9: return SIGKILL;   // SIGKILL
        case -1: return SIGHUP;    // SIGHUP
        case -2: return SIGINT;    // SIGINT
        case -3: return SIGQUIT;   // SIGQUIT
        case -4: return SIGTERM;   // SIGTERM
        case -5: return SIGCHLD;   // SIGCHLD
        case -6: return SIGCONT;   // SIGCONT
        case -7: return SIGWINCH;  // SIGWINCH
        case -8: return SIGTTIN;   // SIGTTIN
        case -9: return SIGTTOU;   // SIGTTOU
        case -10: return SIGTSTP;  // SIGTSTP
        default: return -1;        // Invalid signal
    }
}

// Send a signal to a process
// On Linux, uses pidfd_send_signal syscall if pidfd >= 0, otherwise uses kill
// On other Unix systems, uses kill with the pid parameter
// Returns 0 on success, -1 on error (errno is set)
int send_signal(int pidfd, int pid, int managed_signal) {
    // Map managed signal to native signal number
    int native_signal = map_managed_signal_to_native(managed_signal);
    if (native_signal == -1) {
        errno = EINVAL;
        return -1;
    }
    
#ifdef __linux__
    // On Linux, prefer pidfd_send_signal if we have a valid pidfd
    if (pidfd >= 0) {
        return syscall(__NR_pidfd_send_signal, pidfd, native_signal, NULL, 0);
    } else {
        return kill(pid, native_signal);
    }
#else
    // On other Unix systems, use kill
    (void)pidfd; // Suppress unused parameter warning
    return kill(pid, native_signal);
#endif
}