# libco - Stackful coroutine/fiber backend
# https://github.com/rxi/libco or compatible fork
#
# Provides: low-level context switching for RazorForge suspended routines
# Used by: RazorForge/Suflae green-thread runtime backend
#
# To install:
#   git clone --depth 1 <your-libco-fork> native/libco

set(LIBCO_DIR "${CMAKE_CURRENT_SOURCE_DIR}/libco")

set(LIBCO_SOURCE "")
if(EXISTS "${LIBCO_DIR}/co.c")
    set(LIBCO_SOURCE "${LIBCO_DIR}/co.c")
elseif(EXISTS "${LIBCO_DIR}/libco.c")
    set(LIBCO_SOURCE "${LIBCO_DIR}/libco.c")
endif()

set(LIBCO_HEADER "")
if(EXISTS "${LIBCO_DIR}/co.h")
    set(LIBCO_HEADER "${LIBCO_DIR}/co.h")
elseif(EXISTS "${LIBCO_DIR}/libco.h")
    set(LIBCO_HEADER "${LIBCO_DIR}/libco.h")
endif()

if(LIBCO_SOURCE AND LIBCO_HEADER)
    message(STATUS "Found libco")

    add_library(rf_libco STATIC
        ${LIBCO_SOURCE}
    )

    target_include_directories(rf_libco PUBLIC ${LIBCO_DIR})

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        if(NOT WIN32)
            target_compile_options(rf_libco PRIVATE -fPIC)
        endif()
    endif()

    set(HAVE_LIBCO TRUE)
else()
    add_library(rf_libco INTERFACE)
    set(HAVE_LIBCO FALSE)

    message(STATUS "")
    message(STATUS "libco not found. To enable stackful green-thread contexts:")
    message(STATUS "  clone or vendor libco into native/libco")
    message(STATUS "")
endif()
