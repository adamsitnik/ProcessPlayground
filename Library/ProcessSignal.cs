namespace System.TBA;

// Design: this enum exists because PosixSignal does not expose SIGKILL because it's impossible to register a handler for it.
public enum ProcessSignal
{
    SIGHUP = 1,
    SIGINT = 2,
    SIGQUIT = 3,
    SIGABRT = 6, 
    SIGKILL = 9, // Force kill (cannot be caught/ignored) 
    SIGUSR1 = 10,
    SIGUSR2 = 12,
    SIGPIPE = 13,
    SIGALRM = 14,
    SIGTERM = 15, // graceful shutdown

    // We use Linux values, however the native layer maps them accordingly on other Unix platforms.
    SIGCHLD = 17,
    SIGCONT = 18,
    SIGSTOP = 19,
    SIGTSTP = 20,
}