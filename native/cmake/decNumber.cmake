# decNumber - ANSI C General Decimal Arithmetic Library
# https://github.com/SDL-Hercules-390/decNumber
# License: ICU License
#
# Provides IEEE 754 decimal floating-point:
# - decSingle (decimal32), decDouble (decimal64), decQuad (decimal128)
# - decNumber (arbitrary-precision decimal)
# - ANSI C89, no platform-specific dependencies
#
# Replaces Intel Decimal (libdfp) for D32/D64/D128 and Decimal types.
#
# To install:
#   git clone --depth 1 https://github.com/SDL-Hercules-390/decNumber.git native/decNumber

set(DECNUMBER_DIR "${CMAKE_CURRENT_SOURCE_DIR}/decNumber")
set(DECNUMBER_SRC_DIR "${DECNUMBER_DIR}/source")
set(DECNUMBER_INC_DIR "${DECNUMBER_DIR}/include")

if(EXISTS "${DECNUMBER_SRC_DIR}/decNumber.c" AND EXISTS "${DECNUMBER_INC_DIR}/decNumber.h")
    message(STATUS "Found decNumber library")

    add_library(decnumber STATIC
        ${DECNUMBER_SRC_DIR}/decContext.c
        ${DECNUMBER_SRC_DIR}/decNumber.c
        ${DECNUMBER_SRC_DIR}/decSingle.c
        ${DECNUMBER_SRC_DIR}/decDouble.c
        ${DECNUMBER_SRC_DIR}/decQuad.c
        ${DECNUMBER_SRC_DIR}/decPacked.c
        ${DECNUMBER_SRC_DIR}/decimal32.c
        ${DECNUMBER_SRC_DIR}/decimal64.c
        ${DECNUMBER_SRC_DIR}/decimal128.c
    )

    target_include_directories(decnumber PUBLIC ${DECNUMBER_INC_DIR})

    # Tell decNumber that stdint.h is available (avoids conflicting typedefs)
    target_compile_definitions(decnumber PUBLIC HAVE_STDINT_H)

    # Platform-specific settings
    if(WIN32)
        target_compile_definitions(decnumber PRIVATE _CRT_SECURE_NO_WARNINGS)
    endif()

    # Suppress warnings for third-party code
    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(decnumber PRIVATE -w)
        if(NOT WIN32)
            target_compile_options(decnumber PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(decnumber PRIVATE /W0)
    endif()

    set(HAVE_DECNUMBER TRUE)
else()
    add_library(decnumber INTERFACE)
    set(HAVE_DECNUMBER FALSE)

    message(STATUS "")
    message(STATUS "decNumber not found. To enable decimal floating-point:")
    message(STATUS "  git clone --depth 1 https://github.com/SDL-Hercules-390/decNumber.git native/decNumber")
    message(STATUS "")
endif()