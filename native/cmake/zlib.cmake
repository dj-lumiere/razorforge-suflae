# zlib - Compression Library
# https://github.com/madler/zlib
# License: zlib License
#
# Provides deflate/inflate compression.
# Used by: Compression module (gzip, deflate)
#
# To install:
#   git clone --depth 1 https://github.com/madler/zlib.git native/zlib

set(ZLIB_DIR "${CMAKE_CURRENT_SOURCE_DIR}/zlib")

if(EXISTS "${ZLIB_DIR}/zlib.h")
    message(STATUS "Found zlib")

    add_library(rf_zlib STATIC
        ${ZLIB_DIR}/adler32.c
        ${ZLIB_DIR}/compress.c
        ${ZLIB_DIR}/crc32.c
        ${ZLIB_DIR}/deflate.c
        ${ZLIB_DIR}/gzclose.c
        ${ZLIB_DIR}/gzlib.c
        ${ZLIB_DIR}/gzread.c
        ${ZLIB_DIR}/gzwrite.c
        ${ZLIB_DIR}/infback.c
        ${ZLIB_DIR}/inffast.c
        ${ZLIB_DIR}/inflate.c
        ${ZLIB_DIR}/inftrees.c
        ${ZLIB_DIR}/trees.c
        ${ZLIB_DIR}/uncompr.c
        ${ZLIB_DIR}/zutil.c
    )

    target_include_directories(rf_zlib PUBLIC ${ZLIB_DIR})

    if(WIN32)
        target_compile_definitions(rf_zlib PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(rf_zlib PRIVATE -w)
        if(NOT WIN32)
            target_compile_options(rf_zlib PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(rf_zlib PRIVATE /W0)
    endif()

    set(HAVE_ZLIB TRUE)
else()
    add_library(rf_zlib INTERFACE)
    set(HAVE_ZLIB FALSE)

    message(STATUS "")
    message(STATUS "zlib not found. To enable compression:")
    message(STATUS "  git clone --depth 1 https://github.com/madler/zlib.git native/zlib")
    message(STATUS "")
endif()