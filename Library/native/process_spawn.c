#define _GNU_SOURCE
#include <sys/types.h>
#include <sys/wait.h>
#include <sys/syscall.h>
#include <linux/sched.h>
#include <unistd.h>
#include <fcntl.h>
#include <signal.h>
#include <errno.h>
#include <string.h>
#include <stdint.h>

// Helper to write errno to pipe and exit (ignores write failures)
static inline void write_errno_and_exit(int pipe_fd, int err) {
    // We're about to exit anyway, so ignore write failures
    (void)write(pipe_fd, &err, sizeof(err));
    _exit(127);
}

// clone_args is provided by <linux/sched.h> since kernel 5.3

// Spawns a process using clone3 to get a pidfd atomically
// Returns pidfd on success, -1 on error (errno is set)
int spawn_process_with_pidfd(
    const char* path,
    char* const argv[],
    char* const envp[],
    int stdin_fd,
    int stdout_fd,
    int stderr_fd,
    const char* working_dir)
{
    int wait_pipe[2];
    int pidfd = -1;
    sigset_t all_signals, old_signals;
    
    // Create pipe for exec synchronization (CLOEXEC so child doesn't inherit it)
    if (pipe2(wait_pipe, O_CLOEXEC) != 0) {
        return -1;
    }
    
    // Block all signals before forking
    sigfillset(&all_signals);
    pthread_sigmask(SIG_SETMASK, &all_signals, &old_signals);
    
    // Use clone3 to get pidfd atomically with fork
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
        errno = saved_errno;
        return -1;
    }
    
    if (clone_result == 0) {
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
        
        // Close read end of pipe (we only write)
        close(wait_pipe[0]);
        
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
        execve(path, argv, envp);
        
        // If we get here, execve failed
        write_errno_and_exit(wait_pipe[1], errno);
    }
    
    // ========== PARENT PROCESS ==========
    
    // Restore signal mask
    pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
    
    // Close write end of pipe
    close(wait_pipe[1]);
    
    // Wait for child to exec or fail
    int child_errno = 0;
    ssize_t bytes_read = read(wait_pipe[0], &child_errno, sizeof(child_errno));
    close(wait_pipe[0]);
    
    if (bytes_read == sizeof(child_errno)) {
        // Child failed to exec - reap it
        siginfo_t info;
        waitid(P_PIDFD, pidfd, &info, WEXITED);
        close(pidfd);
        errno = child_errno;
        return -1;
    }
    
    // Success - return pidfd
    return pidfd;
}

// Wait for process to exit and return exit status
// Returns exit status on success, -1 on error (errno is set)
int wait_for_pidfd(int pidfd) {
    siginfo_t info;
    
    while (1) {
        int result = waitid(P_PIDFD, pidfd, &info, WEXITED);
        if (result == 0) {
            close(pidfd);
            return info.si_status;
        }
        
        if (errno != EINTR) {
            return -1;
        }
    }
}

// Kill a process via pidfd
int kill_pidfd(int pidfd, int signal) {
    return syscall(SYS_pidfd_send_signal, pidfd, signal, NULL, 0);
}