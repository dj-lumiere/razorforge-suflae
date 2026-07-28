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

    # LIBCO_MP is LOAD-BEARING for the M:N scheduler. Without it, settings.h defines
    # `thread_local` to EMPTY (its single-threaded default), making libco's co_active_handle /
    # co_active_buffer PROCESS-GLOBAL. With N>1 pool workers each co_switch'ing coroutines, they
    # then clobber one another's saved contexts — a coroutine parks on worker A but its saved
    # resumer context (in the shared buffer) is overwritten by worker B, so the next co_switch
    # jumps into a stale frame → PC corruption / double-resume / SIGSEGV. Defining LIBCO_MP routes
    # settings.h to the real per-thread `thread_local` (via <threads.h>), giving each worker its own
    # active-context handle. (Harmless on Windows, where coro_runtime.c uses fibers, not libco.)
    target_compile_definitions(rf_libco PRIVATE LIBCO_MP)

    # Upstream amd64.c uses the C23 `alignas` keyword. Under the project-wide
    # C11 it only exists via <stdalign.h>, which glibc happens to pull in
    # transitively but MSVC-target builds don't — so compile this target as C23.
    set_target_properties(rf_libco PROPERTIES C_STANDARD 23 C_STANDARD_REQUIRED OFF)

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(rf_libco PRIVATE -w -Wno-implicit-function-declaration)
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
