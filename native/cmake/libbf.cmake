# LibBF - Tiny arbitrary precision floating point library
# Copyright (c) 2017-2025 Fabrice Bellard
# License: MIT
#
# Provides arbitrary precision IEEE 754 floating point with transcendental functions.
# Used for f128 support with full-precision sin, cos, exp, log, etc.
#
# Download from: https://bellard.org/libbf/
# Extract to: native/libbf/

set(LIBBF_DIR "${CMAKE_CURRENT_SOURCE_DIR}/libbf")

# LibBF uses GCC extensions (__attribute__) that MSVC doesn't support
# Only build with Clang or GCC
if(MSVC)
    message(STATUS "LibBF: Skipped (requires Clang or GCC, MSVC not supported)")
    add_library(libbf INTERFACE)
    set(HAVE_LIBBF FALSE)
elseif(EXISTS "${LIBBF_DIR}/libbf.c")
    message(STATUS "Found LibBF")

    add_library(libbf STATIC
        ${LIBBF_DIR}/libbf.c
        ${LIBBF_DIR}/cutils.c
    )

    target_include_directories(libbf PUBLIC ${LIBBF_DIR})

    # Compiler-specific settings (Clang/GCC only)
    target_compile_options(libbf PRIVATE
        -O2
        -Wall
        -Wno-unused-function
        -Wno-unused-variable
    )

    # Link math library on Unix
    if(UNIX AND NOT APPLE)
        target_link_libraries(libbf m)
    endif()

    set(HAVE_LIBBF TRUE)
else()
    add_library(libbf INTERFACE)
    set(HAVE_LIBBF FALSE)

    message(STATUS "")
    message(STATUS "LibBF not found. To enable f128 transcendental functions:")
    message(STATUS "  Download from: https://bellard.org/libbf/")
    message(STATUS "  Extract to: native/libbf/")
    message(STATUS "")
endif()
