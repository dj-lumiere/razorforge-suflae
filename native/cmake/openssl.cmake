# mbedTLS - Lightweight TLS/crypto library (replaces OpenSSL)
# https://github.com/Mbed-TLS/mbedtls
# License: Apache-2.0 / GPL-2.0
#
# Provides: TLS, hashing, encryption, certificates, X.509
# Used by: Crypto module (TLS and certificates)
#
# To install:
#   git clone --depth 1 https://github.com/Mbed-TLS/mbedtls.git native/mbedtls

set(MBEDTLS_DIR "${CMAKE_CURRENT_SOURCE_DIR}/mbedtls2")

if(EXISTS "${MBEDTLS_DIR}/CMakeLists.txt" AND EXISTS "${MBEDTLS_DIR}/include/mbedtls/ssl.h")
    message(STATUS "Found mbedTLS")

    set(ENABLE_TESTING OFF CACHE BOOL "" FORCE)
    set(ENABLE_PROGRAMS OFF CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    set(MBEDTLS_FATAL_WARNINGS OFF CACHE BOOL "" FORCE)

    # mbedTLS uses CMAKE_C_SIMULATE_ID to detect MSVC and adds /W3 /utf-8,
    # which plain clang doesn't understand. Override to use the real compiler ID.
    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" AND CMAKE_C_SIMULATE_ID STREQUAL "MSVC")
        set(_saved_simulate_id "${CMAKE_C_SIMULATE_ID}")
        set(CMAKE_C_SIMULATE_ID "")
    endif()

    add_subdirectory(${MBEDTLS_DIR} ${CMAKE_BINARY_DIR}/mbedtls EXCLUDE_FROM_ALL)

    if(DEFINED _saved_simulate_id)
        set(CMAKE_C_SIMULATE_ID "${_saved_simulate_id}")
        unset(_saved_simulate_id)
    endif()

    set(HAVE_MBEDTLS TRUE)
    # Clear old OpenSSL flag
    set(HAVE_OPENSSL FALSE)
else()
    set(HAVE_MBEDTLS FALSE)
    set(HAVE_OPENSSL FALSE)

    message(STATUS "")
    message(STATUS "mbedTLS not found. To enable TLS/crypto:")
    message(STATUS "  git clone --depth 1 https://github.com/Mbed-TLS/mbedtls.git native/mbedtls")
    message(STATUS "")
endif()