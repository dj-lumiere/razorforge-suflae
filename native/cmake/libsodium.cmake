# libsodium - Modern cryptography library
# https://github.com/jedisct1/libsodium
# License: ISC
#
# Provides: hashing, encryption, signatures, key exchange, random
# Used by: Crypto module
#
# To install:
#   git clone --depth 1 -b stable https://github.com/jedisct1/libsodium.git native/libsodium

set(LIBSODIUM_DIR "${CMAKE_CURRENT_SOURCE_DIR}/libsodium")
set(LIBSODIUM_SRC "${LIBSODIUM_DIR}/src/libsodium")

if(EXISTS "${LIBSODIUM_SRC}/include/sodium.h" AND EXISTS "${LIBSODIUM_SRC}/crypto_aead")
    message(STATUS "Found libsodium")

    # Generate version.h from template
    set(VERSION "1.0.21")
    set(SODIUM_LIBRARY_VERSION_MAJOR 26)
    set(SODIUM_LIBRARY_VERSION_MINOR 3)
    configure_file(
        "${LIBSODIUM_SRC}/include/sodium/version.h.in"
        "${CMAKE_BINARY_DIR}/libsodium/include/sodium/version.h"
        @ONLY
    )

    # Collect all source files
    file(GLOB_RECURSE LIBSODIUM_SOURCES "${LIBSODIUM_SRC}/*.c")

    # Exclude test/benchmark files if any
    list(FILTER LIBSODIUM_SOURCES EXCLUDE REGEX ".*test.*\\.c$")

    add_library(sodium STATIC ${LIBSODIUM_SOURCES})

    target_include_directories(sodium PUBLIC
        ${LIBSODIUM_SRC}/include
        ${CMAKE_BINARY_DIR}/libsodium/include
    )
    target_include_directories(sodium PRIVATE
        ${LIBSODIUM_SRC}/include/sodium
        ${CMAKE_BINARY_DIR}/libsodium/include/sodium
    )

    target_compile_definitions(sodium PRIVATE
        CONFIGURED=1
        SODIUM_STATIC=1
        _CRT_SECURE_NO_WARNINGS
    )

    # Platform-specific defines
    if(WIN32)
        target_compile_definitions(sodium PRIVATE
            NATIVE_LITTLE_ENDIAN=1
            HAVE_WINSOCK2_H=1
            inline=__inline
        )
        target_link_libraries(sodium advapi32)
    else()
        target_compile_definitions(sodium PRIVATE
            HAVE_POSIX_MEMALIGN=1
            HAVE_MMAP=1
            HAVE_MPROTECT=1
            HAVE_MLOCK=1
            HAVE_NANOSLEEP=1
        )
    endif()

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(sodium PRIVATE -w)
        if(NOT WIN32)
            target_compile_options(sodium PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(sodium PRIVATE /W0)
    endif()

    set(HAVE_LIBSODIUM TRUE)
else()
    add_library(sodium INTERFACE)
    set(HAVE_LIBSODIUM FALSE)

    message(STATUS "")
    message(STATUS "libsodium not found. To enable cryptography:")
    message(STATUS "  git clone --depth 1 -b stable https://github.com/jedisct1/libsodium.git native/libsodium")
    message(STATUS "")
endif()
