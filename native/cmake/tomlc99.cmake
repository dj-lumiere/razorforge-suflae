# tomlc99 - TOML v1.0 parser
# https://github.com/cktan/tomlc99
# License: MIT
#
# Provides: TOML parsing (read-only), TOML v1.0 compliant
# Used by: Toml module
#
# To install:
#   git clone --depth 1 https://github.com/cktan/tomlc99.git native/tomlc99

set(TOMLC99_DIR "${CMAKE_CURRENT_SOURCE_DIR}/tomlc99")

if(EXISTS "${TOMLC99_DIR}/toml.c" AND EXISTS "${TOMLC99_DIR}/toml.h")
    message(STATUS "Found tomlc99")

    add_library(rf_tomlc99 STATIC
        ${TOMLC99_DIR}/toml.c
    )

    target_include_directories(rf_tomlc99 PUBLIC ${TOMLC99_DIR})

    if(WIN32)
        target_compile_definitions(rf_tomlc99 PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(rf_tomlc99 PRIVATE -w)
        if(NOT WIN32)
            target_compile_options(rf_tomlc99 PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(rf_tomlc99 PRIVATE /W0)
    endif()

    set(HAVE_TOMLC99 TRUE)
else()
    add_library(rf_tomlc99 INTERFACE)
    set(HAVE_TOMLC99 FALSE)

    message(STATUS "")
    message(STATUS "tomlc99 not found. To enable TOML support:")
    message(STATUS "  git clone --depth 1 https://github.com/cktan/tomlc99.git native/tomlc99")
    message(STATUS "")
endif()
