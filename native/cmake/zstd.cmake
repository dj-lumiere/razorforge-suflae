# Zstandard - Fast real-time compression
# https://github.com/facebook/zstd
# License: BSD + GPLv2
#
# Provides zstd compression/decompression.
# Used by: Compression module
#
# To install:
#   git clone --depth 1 https://github.com/facebook/zstd.git native/zstd

set(ZSTD_DIR "${CMAKE_CURRENT_SOURCE_DIR}/zstd")
set(ZSTD_LIB_DIR "${ZSTD_DIR}/lib")

if(EXISTS "${ZSTD_LIB_DIR}/zstd.h")
    message(STATUS "Found zstd")

    # zstd supports single-file build via ZSTD_UNITY_BUILD
    file(GLOB ZSTD_COMMON_SOURCES "${ZSTD_LIB_DIR}/common/*.c")
    file(GLOB ZSTD_COMPRESS_SOURCES "${ZSTD_LIB_DIR}/compress/*.c")
    file(GLOB ZSTD_DECOMPRESS_SOURCES "${ZSTD_LIB_DIR}/decompress/*.c")

    add_library(rf_zstd STATIC
        ${ZSTD_COMMON_SOURCES}
        ${ZSTD_COMPRESS_SOURCES}
        ${ZSTD_DECOMPRESS_SOURCES}
    )

    target_include_directories(rf_zstd PUBLIC ${ZSTD_LIB_DIR})

    target_compile_definitions(rf_zstd PRIVATE
        ZSTD_MULTITHREAD=0
        ZSTD_LEGACY_SUPPORT=0
    )

    if(WIN32)
        target_compile_definitions(rf_zstd PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(rf_zstd PRIVATE -w)
        if(NOT WIN32)
            target_compile_options(rf_zstd PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(rf_zstd PRIVATE /W0)
    endif()

    set(HAVE_ZSTD TRUE)
else()
    add_library(rf_zstd INTERFACE)
    set(HAVE_ZSTD FALSE)

    message(STATUS "")
    message(STATUS "zstd not found. To enable zstd compression:")
    message(STATUS "  git clone --depth 1 https://github.com/facebook/zstd.git native/zstd")
    message(STATUS "")
endif()