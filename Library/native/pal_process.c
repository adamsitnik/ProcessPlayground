#include "pal_config.h"
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#include <fcntl.h>
#include <signal.h>
#include <errno.h>
#include <string.h>
#include <stdint.h>
#include <poll.h>
#include <time.h>
#include <sys/time.h>

#ifdef HAVE_SYS_SYSCALL_H
#include <sys/syscall.h>
#endif

#ifdef HAVE_LINUX_SCHED_H
#include <linux/sched.h>
#endif

#ifdef HAVE_PDEATHSIG
#include <sys/prctl.h>
#endif

#ifdef HAVE_SYS_EVENT_H
#include <sys/event.h>
#endif

#ifdef HAVE_POSIX_SPAWN
#include <spawn.h>
#endif

// In the future, we could add support for pidfd on FreeBSD
#ifdef HAVE_CLONE3
#define HAVE_PIDFD
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

// Helper function to create a pipe with CLOEXEC flag and optional O_NONBLOCK
static int create_cloexec_pipe(int pipefd[2]) {
#ifdef HAVE_PIPE2
    // On systems with pipe2, use it for atomic CLOEXEC
    return pipe2(pipefd, O_CLOEXEC);
#else
    // On other systems, use pipe + fcntl
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

// Helper function to create a pipe with CLOEXEC flag and optional O_NONBLOCK on either end
// async_read: if non-zero, sets O_NONBLOCK on the read end (pipefd[0])
// async_write: if non-zero, sets O_NONBLOCK on the write end (pipefd[1])
int create_pipe(int pipefd[2], int async_read, int async_write) {
    // First create the pipe with CLOEXEC
    if (create_cloexec_pipe(pipefd) != 0) {
        return -1;
    }
    
    // Set O_NONBLOCK on read end if requested
    if (async_read) {
        int flags = fcntl(pipefd[0], F_GETFL, 0);
        if (flags == -1 || fcntl(pipefd[0], F_SETFL, flags | O_NONBLOCK) == -1) {
            int saved_errno = errno;
            close(pipefd[0]);
            close(pipefd[1]);
            errno = saved_errno;
            return -1;
        }
    }
    
    // Set O_NONBLOCK on write end if requested
    if (async_write) {
        int flags = fcntl(pipefd[1], F_GETFL, 0);
        if (flags == -1 || fcntl(pipefd[1], F_SETFL, flags | O_NONBLOCK) == -1) {
            int saved_errno = errno;
            close(pipefd[0]);
            close(pipefd[1]);
            errno = saved_errno;
            return -1;
        }
    }
    
    return 0;
}

#if defined(HAVE_KQUEUE) || defined(HAVE_KQUEUEX)
int create_kqueue_cloexec(void) {
#ifdef HAVE_KQUEUEX
    // FreeBSD has kqueuex
    return kqueuex(KQUEUE_CLOEXEC);
#else
    // macOS: use kqueue + fcntl
    int queue = kqueue();
    if (queue == -1) {
        return -1;
    }

    // Set CLOEXEC on kqueue
    if (fcntl(queue, F_SETFD, FD_CLOEXEC) == -1) {
        int saved_errno = errno;
        close(queue);
        errno = saved_errno;
        return -1;
    }

    return queue;
#endif
}
#endif

// Spawns a process and returns success/failure
// Returns 0 on success, -1 on error (errno is set)
// If out_pid is not NULL, the PID of the child process is stored there
// If out_pidfd is not NULL, the pidfd of the child process is stored there (Linux only, -1 on other platforms)
// If out_exit_pipe_fd is not NULL, the read end of exit monitoring pipe is stored there
// If kill_on_parent_death is non-zero, the child process will be killed when the parent dies
//   Note: On macOS with posix_spawn, kill_on_parent_death is not supported and will be ignored
// If create_suspended is non-zero, the child process will be created in a suspended state (stopped)
// If create_new_process_group is non-zero, the child process will be created in a new process group
// If inherited_handles is not NULL and inherited_handles_count > 0, the specified file descriptors will be inherited
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
    int kill_on_parent_death,
    int create_suspended,
    int create_new_process_group,
    const int* inherited_handles,
    int inherited_handles_count)
{
#if defined(HAVE_POSIX_SPAWN) && defined(HAVE_POSIX_SPAWN_CLOEXEC_DEFAULT) && defined(HAVE_POSIX_SPAWN_FILE_ACTIONS_ADDINHERIT_NP)
    // ========== POSIX_SPAWN PATH (macOS) ==========
    
    // On macOS without POSIX_SPAWN_START_SUSPENDED, return ENOTSUP early
#ifndef HAVE_POSIX_SPAWN_START_SUSPENDED
    if (create_suspended) {
        errno = ENOTSUP;
        return -1;
    }
#endif
    
    int exit_pipe[2];
    pid_t child_pid;
    posix_spawn_file_actions_t file_actions;
    posix_spawnattr_t attr;
    int result;
    
    // Create pipe for exit monitoring (CLOEXEC to avoid other parallel processes inheriting it)
    if (create_cloexec_pipe(exit_pipe) != 0) {
        return -1;
    }
    
    // Initialize posix_spawn attributes
    if ((result = posix_spawnattr_init(&attr)) != 0) {
        int saved_errno = result;
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // Set flags: POSIX_SPAWN_CLOEXEC_DEFAULT to close all FDs except stdin/stdout/stderr
    // and POSIX_SPAWN_SETSIGDEF to reset signal handlers
    short flags = POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF;
#ifdef HAVE_POSIX_SPAWN_START_SUSPENDED
    // If create_suspended is requested, add the POSIX_SPAWN_START_SUSPENDED flag
    if (create_suspended) {
        flags |= POSIX_SPAWN_START_SUSPENDED;
    }
#endif
    // If create_new_process_group is requested, add the POSIX_SPAWN_SETPGROUP flag
    if (create_new_process_group) {
        flags |= POSIX_SPAWN_SETPGROUP;
    }
    if ((result = posix_spawnattr_setflags(&attr, flags)) != 0) {
        int saved_errno = result;
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // If create_new_process_group is set, configure the process group ID to 0
    // which means the child will become the leader of a new process group
    if (create_new_process_group) {
        if ((result = posix_spawnattr_setpgroup(&attr, 0)) != 0) {
            int saved_errno = result;
            posix_spawnattr_destroy(&attr);
            close(exit_pipe[0]);
            close(exit_pipe[1]);
            errno = saved_errno;
            return -1;
        }
    }
    
    // Reset all signal handlers to default
    sigset_t all_signals;
    sigfillset(&all_signals);
    if ((result = posix_spawnattr_setsigdefault(&attr, &all_signals)) != 0) {
        int saved_errno = result;
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // Initialize file actions
    if ((result = posix_spawn_file_actions_init(&file_actions)) != 0) {
        int saved_errno = result;
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // Redirect stdin/stdout/stderr
    if ((result = posix_spawn_file_actions_adddup2(&file_actions, stdin_fd, 0)) != 0
        || (result = posix_spawn_file_actions_adddup2(&file_actions, stdout_fd, 1)) != 0
        || (result = posix_spawn_file_actions_adddup2(&file_actions, stderr_fd, 2)) != 0)
    {
        int saved_errno = result;
        posix_spawn_file_actions_destroy(&file_actions);
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }

    // Set up exit pipe write end as fd 3 in the child process
    // We use fd 3 for the exit pipe as it's typically unused by standard streams (0-2).
    // With POSIX_SPAWN_CLOEXEC_DEFAULT, all fds except 0,1,2 are automatically closed,
    // so we must explicitly mark fd 3 as inheritable using addinherit_np.
    if ((result = posix_spawn_file_actions_adddup2(&file_actions, exit_pipe[1], 3)) != 0
        || (result = posix_spawn_file_actions_addinherit_np(&file_actions, 3)) != 0)
    {
        int saved_errno = result;
        posix_spawn_file_actions_destroy(&file_actions);
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // Add user-provided inherited handles using posix_spawn_file_actions_addinherit_np
    // This ensures they are not closed by POSIX_SPAWN_CLOEXEC_DEFAULT
    if (inherited_handles != NULL && inherited_handles_count > 0) {
        for (int i = 0; i < inherited_handles_count; i++) {
            int fd = inherited_handles[i];
            // Skip stdio fds and exit pipe fd as they're already handled
            if (fd != 0 && fd != 1 && fd != 2 && fd != 3) {
                if ((result = posix_spawn_file_actions_addinherit_np(&file_actions, fd)) != 0) {
                    int saved_errno = result;
                    posix_spawn_file_actions_destroy(&file_actions);
                    posix_spawnattr_destroy(&attr);
                    close(exit_pipe[0]);
                    close(exit_pipe[1]);
                    errno = saved_errno;
                    return -1;
                }
            }
        }
    }
    
    // Change working directory if specified
    if (working_dir != NULL) {
#ifdef HAVE_POSIX_SPAWN_FILE_ACTIONS_ADDCHDIR_NP
        if ((result = posix_spawn_file_actions_addchdir_np(&file_actions, working_dir)) != 0) {
            int saved_errno = result;
            posix_spawn_file_actions_destroy(&file_actions);
            posix_spawnattr_destroy(&attr);
            close(exit_pipe[0]);
            close(exit_pipe[1]);
            errno = saved_errno;
            return -1;
        }
#else
        // If addchdir_np is not available, fail the spawn request
        // as we cannot fulfill the working directory requirement
        posix_spawn_file_actions_destroy(&file_actions);
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = ENOTSUP;
        return -1;
#endif
    }
    
    // Spawn the process
    // If envp is NULL, use the current environment (environ)
    char* const* env = (envp != NULL) ? envp : environ;
    result = posix_spawn(&child_pid, path, &file_actions, &attr, argv, env);
    
    // Clean up
    posix_spawn_file_actions_destroy(&file_actions);
    posix_spawnattr_destroy(&attr);
    
    // Close write end of exit pipe (child owns it)
    close(exit_pipe[1]);
    
    if (result != 0) {
        // Spawn failed
        close(exit_pipe[0]);
        errno = result;
        return -1;
    }
    
    // Note: kill_on_parent_death is not supported with posix_spawn on macOS
    // macOS does not provide a mechanism to automatically kill a child process when the parent dies
    (void)kill_on_parent_death;
    
    // Success - return PID and exit pipe fd if requested
    if (out_pid != NULL) {
        *out_pid = child_pid;
    }
    if (out_pidfd != NULL) {
        *out_pidfd = -1;  // pidfd not supported on macOS
    }
    if (out_exit_pipe_fd != NULL) {
        *out_exit_pipe_fd = exit_pipe[0];
    }
    return 0;
#else
    // ========== FORK/EXEC PATH (Linux and other Unix systems) ==========
    
    // On non-Linux Unix systems without SYS_tgkill, return ENOTSUP early
#ifndef HAVE_SYS_TGKILL
    if (create_suspended) {
        errno = ENOTSUP;
        return -1;
    }
#endif
    
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
    
#ifdef HAVE_CLONE3
    // On systems with clone3, use it to get pidfd atomically with fork
    struct clone_args args = {0};  // Zero-initialize
    // Note: We cannot use CLONE_VFORK when create_suspended is true, because
    // the child will stop itself before exec, which would deadlock the parent
    args.flags = (create_suspended ? 0 : CLONE_VFORK) | CLONE_PIDFD;
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
    // On systems without clone3, use fork or vfork depending on create_suspended
    // Note: We cannot use vfork when create_suspended is true
    child_pid = create_suspended ? fork() : vfork();
    
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
        
        // If create_new_process_group is enabled, create a new process group
        // setpgid(0, 0) sets the process group ID of the calling process to its own PID
        // making it the leader of a new process group
        if (create_new_process_group) {
            if (setpgid(0, 0) == -1) {
                write_errno_and_exit(wait_pipe[1], errno);
            }
        }
        
        // If kill_on_parent_death is enabled, set up parent death signal
        if (kill_on_parent_death) {
#ifdef HAVE_PDEATHSIG
            // On systems with PR_SET_PDEATHSIG (Linux), use it to set up parent death signal
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
            // On systems without prctl, this feature is not available.
            // We would need a different mechanism (like polling or signals),
            // but for now we'll skip it as it's not straightforward to implement
            // without platform-specific code.
            // This is a limitation on systems without prctl.
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
        
#ifdef HAVE_CLOSE_RANGE
        // On systems with close_range (Linux and FreeBSD), use it to mark all FDs from 4 onwards as CLOEXEC
        // This prevents the child from inheriting unwanted file descriptors
        // FDs 0-2 are stdin/stdout/stderr, fd 3 is our exit pipe
        // We use CLOSE_RANGE_CLOEXEC to set the flag without closing the FDs
        // This must be called AFTER the dup2 calls above, so that if stdin_fd/stdout_fd/stderr_fd
        // are >= 4, they don't get CLOEXEC set before being duplicated to 0/1/2
        syscall(__NR_close_range, 4, ~0U, CLOSE_RANGE_CLOEXEC);
        // Ignore errors - if close_range is not supported, we continue anyway
        
        // Remove CLOEXEC flag from user-provided inherited handles
        // so they are inherited by execve
        if (inherited_handles != NULL && inherited_handles_count > 0) {
            for (int i = 0; i < inherited_handles_count; i++) {
                int fd = inherited_handles[i];
                // Skip stdio fds and exit pipe fd as they're already handled
                // Also skip fds < 4 as they weren't affected by close_range
                if (fd >= 4) {
                    int flags = fcntl(fd, F_GETFD);
                    if (flags != -1) {
                        fcntl(fd, F_SETFD, flags & ~FD_CLOEXEC);
                    }
                }
            }
        }
#endif
        
        // Change working directory if specified
        if (working_dir != NULL) {
            if (chdir(working_dir) == -1) {
                write_errno_and_exit(wait_pipe[1], errno);
            }
        }
        
        // If create_suspended is requested, close wait_pipe and stop ourselves before exec
        // This allows the parent to get our PID and set up monitoring before we start executing
        if (create_suspended) {
            // Close wait_pipe to signal parent that we've successfully reached this point
            close(wait_pipe[1]);
            
#if defined(HAVE_SYS_SYSCALL_H) && defined(HAVE_SYS_TGKILL)
            // On Linux, use tgkill to send SIGSTOP to ourselves
            // This is more reliable than kill(getpid(), SIGSTOP) or pthread_kill
            syscall(SYS_tgkill, getpid(), syscall(SYS_gettid), SIGSTOP);
#else
            // On other Unix systems (non-Linux), use kill with getpid()
            // Note: This may not work as reliably as the Linux approach
            kill(getpid(), SIGSTOP);
#endif
            // When the parent resumes us with SIGCONT, execution continues here
        }
        
        // Execute the program
        // If envp is NULL, use the current environment (environ)
        char* const* env = (envp != NULL) ? envp : environ;
        execve(path, argv, env);
        
        // If we get here, execve failed
        // Only write to wait_pipe if it's still open (not suspended case)
        if (!create_suspended) {
            write_errno_and_exit(wait_pipe[1], errno);
        } else {
            _exit(127);
        }
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
#ifdef HAVE_CLONE3
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
    
    // If create_suspended was requested, wait for the child to stop itself
    if (create_suspended) {
        int status;
        pid_t wait_result;
        
        // Wait for the child to stop (WUNTRACED flag)
        // The child will send itself SIGSTOP before execve
        while ((wait_result = waitpid(child_pid, &status, WUNTRACED)) < 0 && errno == EINTR);
        
        if (wait_result == -1) {
            int saved_errno = errno;
            // Child failed to stop - clean up
#ifdef HAVE_CLONE3
            close(pidfd);
#endif
            close(exit_pipe[0]);
            errno = saved_errno;
            return -1;
        }
        
        // Verify the child is actually stopped
        if (!WIFSTOPPED(status)) {
            // Child didn't stop as expected - this is an error
#ifdef HAVE_CLONE3
            close(pidfd);
#endif
            close(exit_pipe[0]);
            errno = ECHILD;  // Use ECHILD to indicate child state error
            return -1;
        }
        // Child is now stopped and waiting for SIGCONT
    }
    
    // Success - return PID, pidfd, and exit pipe fd if requested
    if (out_pid != NULL) {
        *out_pid = child_pid;
    }
    if (out_pidfd != NULL) {
        *out_pidfd = pidfd;
    }
    if (out_exit_pipe_fd != NULL) {
        *out_exit_pipe_fd = exit_pipe[0];
    }
    return 0;
#endif
}

// Map managed ProcessSignal enum values to native signal numbers
// This function converts ProcessSignal enum values to the actual platform-specific signal numbers
static int map_managed_signal_to_native(int managed_signal) {
    // Map from ProcessSignal enum values (defined in ProcessSignal.cs)
    switch (managed_signal) {
        case 1: return SIGHUP;    // SIGHUP
        case 2: return SIGINT;    // SIGINT
        case 3: return SIGQUIT;   // SIGQUIT
        case 6: return SIGABRT;   // SIGABRT
        case 9: return SIGKILL;   // SIGKILL
        case 10: return SIGUSR1;  // SIGUSR1
        case 12: return SIGUSR2;  // SIGUSR2
        case 13: return SIGPIPE;  // SIGPIPE
        case 14: return SIGALRM;  // SIGALRM
        case 15: return SIGTERM;  // SIGTERM
        case 17: return SIGCHLD;  // SIGCHLD
        case 18: return SIGCONT;  // SIGCONT
        case 19: return SIGSTOP;  // SIGSTOP
        case 20: return SIGTSTP;  // SIGTSTP
        default: return -1;       // Invalid signal
    }
}

static int map_native_signal_to_managed(int native_signal) {
    switch (native_signal) {
        case SIGHUP: return 1;    // SIGHUP
        case SIGINT: return 2;    // SIGINT
        case SIGQUIT: return 3;   // SIGQUIT
        case SIGABRT: return 6;   // SIGABRT
        case SIGKILL: return 9;   // SIGKILL
        case SIGUSR1: return 10;  // SIGUSR1
        case SIGUSR2: return 12;  // SIGUSR2
        case SIGPIPE: return 13;  // SIGPIPE
        case SIGALRM: return 14;  // SIGALRM
        case SIGTERM: return 15;  // SIGTERM
        case SIGCHLD: return 17;  // SIGCHLD
        case SIGCONT: return 18;  // SIGCONT
        case SIGSTOP: return 19;  // SIGSTOP
        case SIGTSTP: return 20;  // SIGTSTP
        default: return -1;       // Invalid signal
    }
}

int send_signal(int pidfd, int pid, int managed_signal) {
    // Map managed signal to native signal number
    int native_signal = map_managed_signal_to_native(managed_signal);
    if (native_signal == -1) {
        errno = EINVAL;
        return -1;
    }

    // EINTR is not listed for both kill and __NR_pidfd_send_signal.
#ifdef HAVE_PIDFD_SEND_SIGNAL
    // On systems with pidfd_send_signal, prefer it if we have a valid pidfd
    if (pidfd >= 0) {
        return syscall(__NR_pidfd_send_signal, pidfd, native_signal, NULL, 0);
    }
#else
    (void)pidfd;
#endif

    return kill(pid, native_signal);
}

#ifndef HAVE_PIDFD
static int map_status(int status, int* out_exitCode, int* out_signal) {
    if (WIFEXITED(status)) {
        *out_exitCode = WEXITSTATUS(status);
        *out_signal = 0;
        return 0;
    }
    else if (WIFSIGNALED(status)) {
        int sig = WTERMSIG(status);
        *out_exitCode = 128 + sig;  // Shell convention for signaled processes, followed by dotnet/runtime for Process
        *out_signal = map_native_signal_to_managed(sig);
        return 0;
    }
    return -1; // Still running or unknown status
}
#else
static int map_status(const siginfo_t* info, int* out_exitCode, int* out_signal) {
    switch (info->si_code)
    {
        case CLD_KILLED: // WIFSIGNALED
        case CLD_DUMPED: // WIFSIGNALED
            *out_exitCode = 128 + info->si_status;
            *out_signal = map_native_signal_to_managed(info->si_status);
            return 0;
        case CLD_EXITED: // WIFEXITED
            *out_exitCode = info->si_status;
            *out_signal = 0;
            return 0;
        default:
            return -1; // Unknown state
    }
}
#endif

// -1 is a valid exit code, so to distinguish between a normal exit code and an error, we return 0 on success and -1 on error
// Returns 0 if process has exited (exit code set), -1 if still running or error occurred
int try_get_exit_code(int pidfd, int pid, int* out_exitCode, int* out_signal) {
    int ret;
#ifdef HAVE_PIDFD
    (void)pid;
    siginfo_t info;
    memset(&info, 0, sizeof(info));
    while ((ret = waitid(P_PIDFD, pidfd, &info, WEXITED | WNOHANG)) < 0 && errno == EINTR);

    if (ret == 0 && info.si_pid != 0) {
        return map_status(&info, out_exitCode, out_signal);
    }
#else
    (void)pidfd;
    int status;
    while ((ret = waitpid(pid, &status, WNOHANG)) < 0 && errno == EINTR);

    if (ret > 0) {
        return map_status(status, out_exitCode, out_signal);
    }
#endif
    // Process still running or error
    return -1;
}

// -1 is a valid exit code, so to distinguish between a normal exit code and an error, we return 0 on success and -1 on error
int wait_for_exit_and_reap(int pidfd, int pid, int* out_exitCode, int* out_signal) {
    int ret;
#ifdef HAVE_PIDFD
    siginfo_t info;
    memset(&info, 0, sizeof(info));
    while ((ret = waitid(P_PIDFD, pidfd, &info, WEXITED)) < 0 && errno == EINTR);

    if (ret != -1) {
        return map_status(&info, out_exitCode, out_signal);
    }
#else
    int status;
    while ((ret = waitpid(pid, &status, 0)) < 0 && errno == EINTR);

    if (ret != -1) {
        return map_status(status, out_exitCode, out_signal);
    }
#endif
    return -1;
}

// Try to wait for exit with timeout, but don't kill the process if timeout occurs
// Returns -1 on error, 1 on timeout, or 0 if process exited.
int try_wait_for_exit(int pidfd, int pid, int exitPipeFd, int timeout_ms, int* out_exitCode, int* out_signal) {
    int ret;
#if defined(HAVE_KQUEUE) || defined(HAVE_KQUEUEX)
    // macOS and FreeBSD have kqueue which can monitor process exit
    int queue = create_kqueue_cloexec();
    if (queue == -1) {
        return -1;
    }

    struct kevent change_list = { 0 };
    change_list.ident = pid;
    change_list.filter = EVFILT_PROC;
    change_list.fflags = NOTE_EXIT;
    change_list.flags = EV_ADD | EV_CLEAR;

    struct kevent event_list = { 0 };

    struct timespec timeout = { 0 };
    timeout.tv_sec = timeout_ms / 1000;
    timeout.tv_nsec = (timeout_ms % 1000) * 1000 * 1000;

    while ((ret = kevent(queue, &change_list, 1, &event_list, 1, &timeout)) < 0 && errno == EINTR);

    if (ret < 0) {
        int saved_errno = errno;
        close(queue);

        // If the target process does not exist at registration time kevent() returns -1 and errno == ESRCH.
        if (errno == ESRCH && try_get_exit_code(pidfd, pid, out_exitCode, out_signal) != -1)
        {
            return 0;
        }
        errno = saved_errno;
        return -1;
    }

    close(queue);
#else
    struct pollfd pfd = { 0 };
#ifdef HAVE_PIDFD
    //  Wait on the process descriptor with poll
    pfd.fd = pidfd;
#else
    // Wait for the child to finish with a timeout by monitoring exit pipe for EOF.
    pfd.fd = exitPipeFd;
#endif
    // To poll a process descriptor, Linux needs POLLIN and FreeBSD needs POLLHUP.
    // There are no side-effects to use both
    pfd.events = POLLHUP | POLLIN;

    while ((ret = poll(&pfd, 1, timeout_ms)) < 0 && errno == EINTR);

    if (ret == -1) { // Error
        return -1;
    }
#endif

    if (ret == 0) {
        return 1; // Indicate timeout (not an error, but process didn't exit)
    }

    // Process exited - collect exit status
    return wait_for_exit_and_reap(pidfd, pid, out_exitCode, out_signal);
}


// -1 is a valid exit code, so to distinguish between a normal exit code and an error, we return 0 on success and -1 on error
int wait_for_exit_or_kill_on_timeout(int pidfd, int pid, int exitPipeFd, int timeout_ms, int* out_exitCode, int* out_signal, int* out_timeout) {
    int ret = try_wait_for_exit(pidfd, pid, exitPipeFd, timeout_ms, out_exitCode, out_signal);
    if (ret != 1) {
        return ret; // Either process exited (0) or error occurred (-1)
    }

    *out_timeout = 1;
    // In the future, we could implement a graceful termination attempt here (e.g., send SIGTERM first)
    // Followed, if still running, by SIGKILL after a short delay.
    ret = send_signal(pidfd, pid, map_native_signal_to_managed(SIGKILL));

    if (ret == -1)
    {
        if (errno == ESRCH) // Process does not exist (same for kill and pidfd_send_signal)
        {
            *out_timeout = 0; // Process already exited between the timeout and the kill attempt
        }
        else
        {
            return -1; // Error sending kill signal
        }
    }

    return wait_for_exit_and_reap(pidfd, pid, out_exitCode, out_signal);
}