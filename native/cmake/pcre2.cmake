# PCRE2 - Perl Compatible Regular Expressions
# https://github.com/PCRE2Project/pcre2
# License: BSD-3-Clause
#
# Provides: regex matching with Unicode support
# Used by: Regex module
#
# To install:
#   git clone --depth 1 https://github.com/PCRE2Project/pcre2.git native/pcre2

set(PCRE2_DIR "${CMAKE_CURRENT_SOURCE_DIR}/pcre2")

if(EXISTS "${PCRE2_DIR}/CMakeLists.txt" AND EXISTS "${PCRE2_DIR}/src/pcre2.h.in")
    message(STATUS "Found PCRE2")

    # PCRE2 has full CMake support — build 32-bit variant for UTF-32 Text
    set(PCRE2_BUILD_PCRE2_8 OFF CACHE BOOL "" FORCE)
    set(PCRE2_BUILD_PCRE2_32 ON CACHE BOOL "" FORCE)
    set(PCRE2_BUILD_PCRE2GREP OFF CACHE BOOL "" FORCE)
    set(PCRE2_BUILD_TESTS OFF CACHE BOOL "" FORCE)
    set(PCRE2_SUPPORT_UNICODE ON CACHE BOOL "" FORCE)
    set(PCRE2_STATIC_PIC ON CACHE BOOL "" FORCE)
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    set(BUILD_STATIC_LIBS ON CACHE BOOL "" FORCE)
    # Disable version script detection — fails with clang on Windows
    set(REQUIRE_VSCRIPT "OFF" CACHE STRING "" FORCE)
    add_subdirectory(${PCRE2_DIR} ${CMAKE_BINARY_DIR}/pcre2 EXCLUDE_FROM_ALL)

    set(HAVE_PCRE2 TRUE)
else()
    add_library(rf_pcre2 INTERFACE)
    set(HAVE_PCRE2 FALSE)

    message(STATUS "")
    message(STATUS "PCRE2 not found. To enable regex:")
    message(STATUS "  git clone --depth 1 https://github.com/PCRE2Project/pcre2.git native/pcre2")
    message(STATUS "")
endif()