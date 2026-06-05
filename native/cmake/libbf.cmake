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

    # libbf exports global mp_add / mp_sub / mp_mul (limb-array multi-precision helpers) whose names
    # COLLIDE with LibTomMath's public mp_add / mp_sub / mp_mul (mp_int* API). In the shared runtime DLL
    # the linker can bind bignum_functions.c's LibTomMath calls to libbf's versions, which reinterpret the
    # mp_int as a limb array with a garbage length -> out-of-bounds read -> heap corruption (numeric_arbitrary
    # AccessViolation). Namespace libbf's internal copies to remove the clash. The rename is applied to every
    # libbf translation unit (and the libbf.h declarations it pulls in), and no non-libbf runtime code calls
    # libbf's mp_*; the only runtime caller (bignum_functions.c) wants LibTomMath's mp_*, which is unaffected.
    target_compile_definitions(libbf PRIVATE
        mp_add=libbf_mp_add
        mp_sub=libbf_mp_sub
        mp_mul=libbf_mp_mul
    )

    # Compiler-specific settings (Clang/GCC only)
    target_compile_options(libbf PRIVATE
        -O2
        -Wall
        -Wno-unused-function
        -Wno-unused-variable
    )
    # -fPIC is needed for shared libraries on Unix, but not on Windows
    if(NOT WIN32)
        target_compile_options(libbf PRIVATE -fPIC)
    endif()

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
