# utf8proc - Unicode processing library
# https://github.com/JuliaStrings/utf8proc
# License: MIT
#
# Provides: Unicode character properties, normalization (NFC/NFD/NFKC/NFKD),
#           case mapping, category lookup — all by codepoint (works with UTF-32 Text)
# Used by: Core Unicode support
#
# To install:
#   git clone --depth 1 https://github.com/JuliaStrings/utf8proc.git native/utf8proc

set(UTF8PROC_DIR "${CMAKE_CURRENT_SOURCE_DIR}/utf8proc")

if(EXISTS "${UTF8PROC_DIR}/utf8proc.c" AND EXISTS "${UTF8PROC_DIR}/utf8proc.h")
    message(STATUS "Found utf8proc")

    add_library(rf_utf8proc STATIC
        ${UTF8PROC_DIR}/utf8proc.c
    )

    target_include_directories(rf_utf8proc PUBLIC ${UTF8PROC_DIR})

    target_compile_definitions(rf_utf8proc PRIVATE
        UTF8PROC_STATIC=1
    )

    if(WIN32)
        target_compile_definitions(rf_utf8proc PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(rf_utf8proc PRIVATE -w)
        if(NOT WIN32)
            target_compile_options(rf_utf8proc PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(rf_utf8proc PRIVATE /W0)
    endif()

    set(HAVE_UTF8PROC TRUE)
else()
    add_library(rf_utf8proc INTERFACE)
    set(HAVE_UTF8PROC FALSE)

    message(STATUS "")
    message(STATUS "utf8proc not found. To enable Unicode support:")
    message(STATUS "  git clone --depth 1 https://github.com/JuliaStrings/utf8proc.git native/utf8proc")
    message(STATUS "")
endif()