cmake_minimum_required(VERSION 3.20)
project(pal_process C)

# Set C standard
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# Run configuration checks
include(configure.cmake)

# Define the library name
set(LIBRARY_NAME "pal_process")

# Detect platform and set library extension
if(APPLE)
    set(LIBRARY_OUTPUT_NAME "lib${LIBRARY_NAME}.dylib")
else()
    set(LIBRARY_OUTPUT_NAME "lib${LIBRARY_NAME}.so")
endif()

# Add source files
add_library(${LIBRARY_NAME} SHARED
    pal_process.c
)

# Add compiler flags
target_compile_options(${LIBRARY_NAME} PRIVATE
    -Wall
    -O2
    -fPIC
)

# Include the build directory for generated headers
target_include_directories(${LIBRARY_NAME} PRIVATE
    ${CMAKE_CURRENT_BINARY_DIR}
)

# Link pthread
target_link_libraries(${LIBRARY_NAME} PRIVATE pthread)

# Set output name to match expected library name
set_target_properties(${LIBRARY_NAME} PROPERTIES
    OUTPUT_NAME ${LIBRARY_NAME}
    PREFIX "lib"
)

# For macOS, set the dylib suffix
if(APPLE)
    set_target_properties(${LIBRARY_NAME} PROPERTIES
        SUFFIX ".dylib"
    )
else()
    set_target_properties(${LIBRARY_NAME} PROPERTIES
        SUFFIX ".so"
    )
endif()

# Installation rules (optional)
install(TARGETS ${LIBRARY_NAME}
    LIBRARY DESTINATION lib
    RUNTIME DESTINATION bin
)
