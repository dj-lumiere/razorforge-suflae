# yyjson - Fast JSON parser/writer
# https://github.com/ibireme/yyjson
# License: MIT
#
# Provides: JSON read/write/mutate, fastest JSON parser in C
# Used by: Json module
#
# To install:
#   git clone --depth 1 https://github.com/ibireme/yyjson.git native/yyjson

set(YYJSON_DIR "${CMAKE_CURRENT_SOURCE_DIR}/yyjson")

if(EXISTS "${YYJSON_DIR}/src/yyjson.c" AND EXISTS "${YYJSON_DIR}/src/yyjson.h")
    message(STATUS "Found yyjson")

    add_library(rf_yyjson STATIC
        ${YYJSON_DIR}/src/yyjson.c
    )

    target_include_directories(rf_yyjson PUBLIC ${YYJSON_DIR}/src)

    if(WIN32)
        target_compile_definitions(rf_yyjson PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(rf_yyjson PRIVATE -w)
        if(NOT WIN32)
            target_compile_options(rf_yyjson PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(rf_yyjson PRIVATE /W0)
    endif()

    set(HAVE_YYJSON TRUE)
else()
    add_library(rf_yyjson INTERFACE)
    set(HAVE_YYJSON FALSE)

    message(STATUS "")
    message(STATUS "yyjson not found. To enable JSON support:")
    message(STATUS "  git clone --depth 1 https://github.com/ibireme/yyjson.git native/yyjson")
    message(STATUS "")
endif()
