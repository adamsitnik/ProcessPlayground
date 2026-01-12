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

// Helper function to create a pipe with CLOEXEC flag
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

#if defined(HAVE_KQUEUE) || defined(HAVE_KQUEUEX)
static inline int create_kqueue_cloexec(void) {
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
#if defined(HAVE_POSIX_SPAWN) && defined(HAVE_POSIX_SPAWN_CLOEXEC_DEFAULT) && defined(HAVE_POSIX_SPAWN_FILE_ACTIONS_ADDINHERIT_NP)
    // ========== POSIX_SPAWN PATH (macOS) ==========
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
    if ((result = posix_spawnattr_setflags(&attr, flags)) != 0) {
        int saved_errno = result;
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
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
    if (stdin_fd != 0) {
        if ((result = posix_spawn_file_actions_adddup2(&file_actions, stdin_fd, 0)) != 0) {
            int saved_errno = result;
            posix_spawn_file_actions_destroy(&file_actions);
            posix_spawnattr_destroy(&attr);
            close(exit_pipe[0]);
            close(exit_pipe[1]);
            errno = saved_errno;
            return -1;
        }
    }
    
    if (stdout_fd != 1) {
        if ((result = posix_spawn_file_actions_adddup2(&file_actions, stdout_fd, 1)) != 0) {
            int saved_errno = result;
            posix_spawn_file_actions_destroy(&file_actions);
            posix_spawnattr_destroy(&attr);
            close(exit_pipe[0]);
            close(exit_pipe[1]);
            errno = saved_errno;
            return -1;
        }
    }
    
    if (stderr_fd != 2) {
        if ((result = posix_spawn_file_actions_adddup2(&file_actions, stderr_fd, 2)) != 0) {
            int saved_errno = result;
            posix_spawn_file_actions_destroy(&file_actions);
            posix_spawnattr_destroy(&attr);
            close(exit_pipe[0]);
            close(exit_pipe[1]);
            errno = saved_errno;
            return -1;
        }
    }
    
    // Duplicate exit pipe write end to fd 3
    // First, we need to clear CLOEXEC on exit_pipe[1] temporarily
    // Actually, we can't do that with posix_spawn directly, so we need to dup it to fd 3
    // But posix_spawn will close all fds except 0,1,2 due to POSIX_SPAWN_CLOEXEC_DEFAULT
    // So we need to use posix_spawn_file_actions_addinherit_np to keep fd 3 open
    
    // First dup the exit pipe write end to fd 3
    if ((result = posix_spawn_file_actions_adddup2(&file_actions, exit_pipe[1], 3)) != 0) {
        int saved_errno = result;
        posix_spawn_file_actions_destroy(&file_actions);
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
    }
    
    // Now mark fd 3 as inheritable (exempt from POSIX_SPAWN_CLOEXEC_DEFAULT)
    // This is a macOS-specific extension to keep fd 3 open
    extern int posix_spawn_file_actions_addinherit_np(posix_spawn_file_actions_t *, int);
    if ((result = posix_spawn_file_actions_addinherit_np(&file_actions, 3)) != 0) {
        int saved_errno = result;
        posix_spawn_file_actions_destroy(&file_actions);
        posix_spawnattr_destroy(&attr);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        errno = saved_errno;
        return -1;
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
        // If addchdir_np is not available, we cannot change directory
        // This is a limitation, but we'll continue
        // The caller will need to handle this limitation
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
    // This is a known limitation as we can't use PR_SET_PDEATHSIG
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
    // On systems without clone3, use vfork
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

int send_signal(int pidfd, int pid, int managed_signal) {
    // Map managed signal to native signal number
    int native_signal = map_managed_signal_to_native(managed_signal);
    if (native_signal == -1) {
        errno = EINVAL;
        return -1;
    }
    
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
int map_status(int status, int* out_exitCode) {
    if (WIFEXITED(status)) {
        *out_exitCode = WEXITSTATUS(status);
        return 0;
    }
    else if (WIFSIGNALED(status)) {
        // Child was killed by signal - return 128 + signal number (common convention)
        // *out_exitCode = 128 + WTERMSIG(status);
        *out_exitCode = -1;
        return 0;
    }
    return -1; // Still running or unknown status
}
#endif

// -1 is a valid exit code, so to distinguish between a normal exit code and an error, we return 0 on success and -1 on error
// Returns 0 if process has exited (exit code set), -1 if still running or error occurred
int try_get_exit_code(int pidfd, int pid, int* out_exitCode) {
    int ret;
#ifdef HAVE_PIDFD
    (void)pid;
    siginfo_t info;
    memset(&info, 0, sizeof(info));
    while ((ret = waitid(P_PIDFD, pidfd, &info, WEXITED | WNOHANG)) < 0 && errno == EINTR);

    if (ret == 0 && info.si_pid != 0) {
        *out_exitCode = info.si_status;
        return 0;
    }
#else
    (void)pidfd;
    int status;
    while ((ret = waitpid(pid, &status, WNOHANG)) < 0 && errno == EINTR);

    if (ret > 0) {
        return map_status(status, out_exitCode);
    }
#endif
    // Process still running or error
    return -1;
}

// -1 is a valid exit code, so to distinguish between a normal exit code and an error, we return 0 on success and -1 on error
int wait_for_exit(int pidfd, int pid, int timeout_ms, int* out_exitCode) {
    int ret;
#ifdef HAVE_PIDFD
    if (timeout_ms >= 0) {
        struct pollfd pfd = { 0 };
        pfd.fd = pidfd;
        pfd.events = POLLIN;

        while ((ret = poll(&pfd, 1, timeout_ms)) < 0 && errno == EINTR);

        if (ret == -1) { // Error
            return -1;
        }
        else if (ret == 0) { // Timeout
            send_signal(pidfd, pid, SIGKILL);
        }
    }

    siginfo_t info;
    memset(&info, 0, sizeof(info));
    while ((ret = waitid(P_PIDFD, pidfd, &info, WEXITED)) < 0 && errno == EINTR);

    if (ret != -1) {
        *out_exitCode = info.si_status;
        return 0;
    }
#else

#if defined(HAVE_KQUEUE) || defined(HAVE_KQUEUEX)
    if (timeout_ms >= 0) {
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

        // For EVFILT_PROC with NOTE_EXIT, if the target process is already gone:
        // - There is no process,
        // - Therefore there is no exit event to detect,
        // - So no kevent is generated.
        // So, first check if the process has already exited before registering the kevent.
        if (try_get_exit_code(pidfd, pid, out_exitCode) != -1) {
            close(queue);
            return 0;
        }

        while ((ret = kevent(queue, &change_list, 1, &event_list, 1, &timeout)) < 0 && errno == EINTR);

        if (ret < 0) {
            int saved_errno = errno;
            close(queue);
            errno = saved_errno;
            return -1;
        }

        // kqueue is stateful, we need to delete the event.
        // We could use EV_ONESHOT, but it would not handle timeout (no event was consumed).
        change_list.flags = EV_DELETE;
        kevent(queue, &change_list, 1, NULL, 0, NULL);
        close(queue);

        if (ret == 0) { // Timeout
            kill(pid, SIGKILL);
        }
    }
#endif

    int status;
    while ((ret = waitpid(pid, &status, 0)) < 0 && errno == EINTR);

    if (ret != -1) {
        return map_status(status, out_exitCode);
    }
#endif
    return -1;
}