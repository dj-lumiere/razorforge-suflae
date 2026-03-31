# minicsv - Tiny CSV parser
# https://github.com/jedisct1/minicsv
# License: BSD
#
# Provides: CSV parsing, no heap allocations, handles quoted fields/multiline
# Used by: Csv module
#
# To install:
#   git clone --depth 1 https://github.com/jedisct1/minicsv.git native/minicsv

set(MINICSV_DIR "${CMAKE_CURRENT_SOURCE_DIR}/minicsv")

if(EXISTS "${MINICSV_DIR}/minicsv.c" AND EXISTS "${MINICSV_DIR}/minicsv.h")
    message(STATUS "Found minicsv")

    add_library(rf_minicsv STATIC
        ${MINICSV_DIR}/minicsv.c
    )

    target_include_directories(rf_minicsv PUBLIC ${MINICSV_DIR})

    if(WIN32)
        target_compile_definitions(rf_minicsv PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(rf_minicsv PRIVATE -w)
        if(NOT WIN32)
            target_compile_options(rf_minicsv PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(rf_minicsv PRIVATE /W0)
    endif()

    set(HAVE_MINICSV TRUE)
else()
    add_library(rf_minicsv INTERFACE)
    set(HAVE_MINICSV FALSE)

    message(STATUS "")
    message(STATUS "minicsv not found. To enable CSV support:")
    message(STATUS "  git clone --depth 1 https://github.com/jedisct1/minicsv.git native/minicsv")
    message(STATUS "")
endif()
