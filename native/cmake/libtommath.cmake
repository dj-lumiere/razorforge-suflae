# LibTomMath - Arbitrary precision integer arithmetic
# https://github.com/libtom/libtommath
# License: Public Domain (Unlicense)
#
# LibTomMath provides:
# - Arbitrary precision integer arithmetic
# - Modular arithmetic, GCD, LCM
# - Prime number generation and testing
# - Used by: RazorForge's bigint type
#
# To install:
#   git clone https://github.com/libtom/libtommath.git native/libtommath

set(LIBTOMMATH_DIR "${CMAKE_CURRENT_SOURCE_DIR}/libtommath")

# Check for LibTomMath source structure from GitHub
if(EXISTS "${LIBTOMMATH_DIR}/tommath.h")
    message(STATUS "Found LibTomMath sources")

    # Collect all source files (mp_*.c, s_mp_*.c are the main library sources)
    file(GLOB LIBTOMMATH_SOURCES
        "${LIBTOMMATH_DIR}/mp_*.c"
        "${LIBTOMMATH_DIR}/s_mp_*.c"
        "${LIBTOMMATH_DIR}/bn_*.c"
    )

    # Also include tommath.c if it exists (single-file build)
    if(EXISTS "${LIBTOMMATH_DIR}/tommath.c")
        list(APPEND LIBTOMMATH_SOURCES "${LIBTOMMATH_DIR}/tommath.c")
    endif()

    if(LIBTOMMATH_SOURCES)
        add_library(tommath STATIC ${LIBTOMMATH_SOURCES})

        target_include_directories(tommath PUBLIC ${LIBTOMMATH_DIR})

        # Platform-specific configurations
        if(WIN32)
            target_compile_definitions(tommath PRIVATE _CRT_SECURE_NO_WARNINGS)
        endif()

        # Optimization for release builds
        if(CMAKE_BUILD_TYPE STREQUAL "Release")
            target_compile_definitions(tommath PRIVATE MP_NO_FILE)
        endif()

        # -fPIC is needed for shared libraries on Unix, but not on Windows
        if(NOT WIN32 AND CMAKE_C_COMPILER_ID STREQUAL "GNU")
            target_compile_options(tommath PRIVATE -fPIC)
        endif()

        set(HAVE_LIBTOMMATH TRUE)
    else()
        add_library(tommath INTERFACE)
        set(HAVE_LIBTOMMATH FALSE)
    endif()

else()
    # No LibTomMath sources found - create interface library
    add_library(tommath INTERFACE)
    set(HAVE_LIBTOMMATH FALSE)

    message(STATUS "")
    message(STATUS "LibTomMath not found. To enable arbitrary precision integers:")
    message(STATUS "  git clone https://github.com/libtom/libtommath.git native/libtommath")
    message(STATUS "")
endif()
