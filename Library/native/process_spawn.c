#define _GNU_SOURCE
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>
#include <fcntl.h>
#include <signal.h>
#include <errno.h>
#include <string.h>
#include <stdint.h>
#include <pthread.h>

#ifdef __linux__
#include <sys/syscall.h>
#include <linux/sched.h>

// waitid constants
#ifndef WSTOPPED
#define WSTOPPED 0x00000002
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
// If create_suspended is non-zero, the child will block on a pipe read before execve
// If out_resume_pipe_fd is not NULL and create_suspended is non-zero, the write end of the resume pipe is stored there
int spawn_process(
    const char* path,
    char* const argv[],
    char* const envp[],
    int stdin_fd,
    int stdout_fd,
    int stderr_fd,
    const char* working_dir,
    int create_suspended,
    int* out_pid,
    int* out_pidfd,
    int* out_exit_pipe_fd,
    int* out_resume_pipe_fd)
{
    int wait_pipe[2];
    int exit_pipe[2];
    int resume_pipe[2] = {-1, -1};
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
    
    // Create resume pipe if create_suspended is requested
    // Note: This pipe should NOT have CLOEXEC so the child can use it
    if (create_suspended) {
        if (pipe(resume_pipe) != 0) {
            int saved_errno = errno;
            close(wait_pipe[0]);
            close(wait_pipe[1]);
            close(exit_pipe[0]);
            close(exit_pipe[1]);
            errno = saved_errno;
            return -1;
        }
    }
    
    // Block all signals before forking
    sigfillset(&all_signals);
    pthread_sigmask(SIG_SETMASK, &all_signals, &old_signals);
    
    pid_t child_pid;
    
#ifdef __linux__
    // On Linux, use clone3 to get pidfd atomically with fork
    struct clone_args args = {0};  // Zero-initialize
    // Use CLONE_VFORK for performance unless create_suspended is requested
    // With create_suspended, we need the parent to wait for SIGSTOP, not vfork semantics
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
        if (resume_pipe[0] != -1) {
            close(resume_pipe[0]);
            close(resume_pipe[1]);
        }
        errno = saved_errno;
        return -1;
    }
    
    child_pid = (pid_t)clone_result;
    
    if (clone_result == 0) {
#else
    // On non-Linux Unix, use regular fork when create_suspended, otherwise vfork for performance
    child_pid = create_suspended ? fork() : vfork();
    
    if (child_pid == -1) {
        // Fork failed
        int saved_errno = errno;
        pthread_sigmask(SIG_SETMASK, &old_signals, NULL);
        close(wait_pipe[0]);
        close(wait_pipe[1]);
        close(exit_pipe[0]);
        close(exit_pipe[1]);
        if (resume_pipe[0] != -1) {
            close(resume_pipe[0]);
            close(resume_pipe[1]);
        }
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
        
        // If create_suspended, block on resume pipe before execve
        if (create_suspended) {
            // Close write end of resume pipe (parent owns it)
            close(resume_pipe[1]);
            
            // Perform blocking read from resume pipe
            // This will block until parent writes to the pipe (Resume() is called)
            char dummy;
            (void)read(resume_pipe[0], &dummy, 1);
            
            // Close read end after unblocking
            close(resume_pipe[0]);
            
            // If read failed (shouldn't happen in normal operation), proceed anyway
            // The process will exec regardless
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
    
    // Close read end of resume pipe if created (parent only needs write end)
    if (resume_pipe[0] != -1) {
        close(resume_pipe[0]);
    }
    
    // If create_suspended, we don't wait for the wait_pipe because the child
    // will be blocked on the resume pipe before exec. We'll return immediately.
    if (!create_suspended) {
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
            if (resume_pipe[1] != -1) {
                close(resume_pipe[1]);
            }
            errno = child_errno;
            return -1;
        }
    } else {
        // For suspended processes, close the wait pipe immediately
        close(wait_pipe[0]);
    }
    
    // Success - return PID, pidfd, exit pipe fd, and resume pipe fd if requested
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
    if (out_resume_pipe_fd != NULL) {
        *out_resume_pipe_fd = resume_pipe[1];
    }
    return 0;
}