# Configure script to detect platform features
# This generates pal_config.h with feature detection macros

include(CheckSymbolExists)
include(CheckIncludeFile)

# Set _GNU_SOURCE for feature test macros (needed for pipe2 on Linux)
add_definitions(-D_GNU_SOURCE)

# Check for pipe2 function (Linux, some BSDs)
set(CMAKE_REQUIRED_DEFINITIONS -D_GNU_SOURCE)
check_symbol_exists(pipe2 "unistd.h;fcntl.h" HAVE_PIPE2)
unset(CMAKE_REQUIRED_DEFINITIONS)

# Check for necessary headers
check_include_file("sys/syscall.h" HAVE_SYS_SYSCALL_H)
check_include_file("linux/sched.h" HAVE_LINUX_SCHED_H)
check_include_file("sys/event.h" HAVE_SYS_EVENT_H)

# Check for kqueue (macOS, FreeBSD, and other BSDs)
if(CMAKE_SYSTEM_NAME STREQUAL "Darwin" OR CMAKE_SYSTEM_NAME STREQUAL "FreeBSD")
    check_symbol_exists(kqueue "sys/event.h" HAVE_KQUEUE)
endif()

# Check for kqueuex (FreeBSD-specific extended kqueue)
if(CMAKE_SYSTEM_NAME STREQUAL "FreeBSD")
    check_symbol_exists(kqueuex "sys/event.h" HAVE_KQUEUEX)
endif()

# On Linux, check for specific syscalls
# We can't directly check for syscalls, but we can check if the headers define them
if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    # Check if we can compile code using clone3
    include(CheckCSourceCompiles)
    
    check_c_source_compiles("
        #include <sys/syscall.h>
        #include <linux/sched.h>
        int main() {
            #ifdef SYS_clone3
            return 0;
            #else
            #error SYS_clone3 not defined
            #endif
        }
    " HAVE_CLONE3)
    
    check_c_source_compiles("
        #include <sys/syscall.h>
        int main() {
            #ifdef __NR_pidfd_send_signal
            return 0;
            #else
            #error __NR_pidfd_send_signal not defined
            #endif
        }
    " HAVE_PIDFD_SEND_SIGNAL_SYSCALL)
    
    check_c_source_compiles("
        #include <sys/syscall.h>
        int main() {
            #ifdef __NR_close_range
            return 0;
            #else
            #error __NR_close_range not defined
            #endif
        }
    " HAVE_CLOSE_RANGE_SYSCALL)

    check_c_source_compiles("
        #include <sys/prctl.h>
        int main() {
            #ifdef PR_SET_PDEATHSIG
            return 0;
            #else
            #error PR_SET_PDEATHSIG not defined
            #endif
        }
    " HAVE_PDEATHSIG)

endif()

# Generate the configuration header
configure_file(
    ${CMAKE_CURRENT_SOURCE_DIR}/pal_config.h.in
    ${CMAKE_CURRENT_BINARY_DIR}/pal_config.h
)