cmake_minimum_required(VERSION 3.20)
project(RazorForgeNative C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

# Export all symbols on Windows (generates .lib import library)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS ON)

# Platform-specific library suffix
if(WIN32)
    set(LIB_SUFFIX ".dll")
    set(LIB_PREFIX "")
elseif(APPLE)
    set(LIB_SUFFIX ".dylib")
    set(LIB_PREFIX "lib")
else()
    set(LIB_SUFFIX ".so")
    set(LIB_PREFIX "lib")
endif()

# Include directories
include_directories(include)

# ============================================================================
# Third-party libraries — Core (always needed)
# ============================================================================

# (No arbitrary-precision C library: decNumber, TLFloat and LibBF are all retired. D32/D64/D128,
#  B128, Integer and Real are pure-RF software engines (Integer and Real are a port of LibBF's
#  algorithms), B128 text parsing/formatting is pure RF (FloatConvert.rf), and the builder encodes
#  B128/Integer literals in managed C# (NumericLiteralParser.EncodeB128). Algorithm credit retained
#  in docs/licenses.)

# ============================================================================
# Third-party libraries — Modules (loaded on demand)
# ============================================================================

# zlib - Compression (deflate/gzip)
# https://github.com/madler/zlib (zlib License)
include(cmake/zlib.cmake)

# zstd - Fast real-time compression
# https://github.com/facebook/zstd (BSD + GPLv2)
include(cmake/zstd.cmake)

# SQLite3 - Embedded SQL database
# https://www.sqlite.org/ (Public Domain)
include(cmake/sqlite3.cmake)

# libsodium - Modern cryptography
# https://github.com/jedisct1/libsodium (ISC)
include(cmake/libsodium.cmake)

# mbedTLS - Lightweight TLS/crypto (replaces OpenSSL)
# https://github.com/Mbed-TLS/mbedtls (Apache-2.0)
include(cmake/openssl.cmake)

# PCRE2 - Regular expressions with Unicode
# https://github.com/PCRE2Project/pcre2 (BSD-3-Clause)
include(cmake/pcre2.cmake)

# utf8proc - Unicode properties, normalization, case mapping
# https://github.com/JuliaStrings/utf8proc (MIT)
include(cmake/utf8proc.cmake)

# yyjson - Fast JSON parser/writer
# https://github.com/ibireme/yyjson (MIT)
include(cmake/yyjson.cmake)

# tomlc99 - TOML v1.0 parser
# https://github.com/cktan/tomlc99 (MIT)
include(cmake/tomlc99.cmake)

# minicsv - Tiny CSV parser
# https://github.com/jedisct1/minicsv (BSD)
include(cmake/minicsv.cmake)

# libco - Stackful coroutine/fiber backend
# https://github.com/rxi/libco or compatible fork (public domain / ISC-like depending on fork)
include(cmake/libco.cmake)

# libuv - Cross-platform async I/O and event loop
# https://github.com/libuv/libuv (MIT)
include(cmake/libuv.cmake)


# ============================================================================
# Compiler-RT shim for 128-bit integer operations (Windows)
# The runtime uses __int128 (rf_divide.c) which requires __udivti3 etc. from compiler-rt/libgcc
# ============================================================================
if(WIN32)
    add_library(compiler_rt_shim STATIC
        runtime/compiler_rt_shim.c
    )
    # Ensure these symbols are exported
    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(compiler_rt_shim PRIVATE -fvisibility=default)
    endif()
endif()

# ============================================================================
# Runtime library for memory management, error handling, and math
# ============================================================================
add_library(razorforge_runtime SHARED
    runtime/runtime_init.c
    runtime/cstring_runtime.c
    runtime/console_runtime.c
    runtime/duration_wait.c
    runtime/crash_runtime.c
    runtime/shared_locks.c
    runtime/memory.c
    runtime/stacktrace.c
    runtime/moment.c
    runtime/math_functions.c
    runtime/rf_divide.c
    runtime/text_functions.c
    runtime/file_functions.c
    runtime/task_runtime.c
    runtime/concurrency_context.c
    runtime/coro_runtime.c
    runtime/async_io.c
    runtime/channel_runtime.c
    runtime/signal_runtime.c
    runtime/process_signals.c
    runtime/random.c
    runtime/emutls_runtime.c
    runtime/ffi_callback_test.c
    runtime/types.h
)

target_include_directories(razorforge_runtime PUBLIC include)

# Link math library
if(UNIX AND NOT APPLE)
    target_link_libraries(razorforge_runtime m)
endif()

# Link DbgHelp for stack trace symbol resolution on Windows
if(WIN32)
    target_link_libraries(razorforge_runtime dbghelp)
endif()

# On Windows, compile the compiler-rt shim (128-bit integer division builtins) straight into the
# runtime DLL. Linking it as a static library would only pull its object in when something in the
# DLL itself references __udivti3 etc., and then the builtins would be missing from the export
# table the in-process ORC JIT resolves against.
if(WIN32)
    target_sources(razorforge_runtime PRIVATE runtime/compiler_rt_shim.c)
    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        set_source_files_properties(runtime/compiler_rt_shim.c PROPERTIES COMPILE_OPTIONS -fvisibility=default)
    endif()
endif()

# Link zlib if available
if(HAVE_ZLIB)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_ZLIB)
    target_link_libraries(razorforge_runtime rf_zlib)
endif()

# Link zstd if available
if(HAVE_ZSTD)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_ZSTD)
    target_link_libraries(razorforge_runtime rf_zstd)
endif()

# Link sqlite3 if available
if(HAVE_SQLITE3)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_SQLITE3)
    target_link_libraries(razorforge_runtime rf_sqlite3)
endif()

# Link libsodium if available
if(HAVE_LIBSODIUM)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_LIBSODIUM)
    target_link_libraries(razorforge_runtime sodium)
endif()

# Link mbedTLS if available
if(HAVE_MBEDTLS)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_MBEDTLS)
    target_link_libraries(razorforge_runtime mbedtls mbedcrypto mbedx509)
endif()

# Link PCRE2 if available
if(HAVE_PCRE2)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_PCRE2 PCRE2_STATIC PCRE2_CODE_UNIT_WIDTH=32)
    target_link_libraries(razorforge_runtime pcre2-32-static)
endif()

# Link utf8proc if available
if(HAVE_UTF8PROC)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_UTF8PROC UTF8PROC_STATIC)
    target_link_libraries(razorforge_runtime rf_utf8proc)
endif()

# Link yyjson if available
if(HAVE_YYJSON)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_YYJSON)
    target_link_libraries(razorforge_runtime rf_yyjson)
endif()

# Link tomlc99 if available
if(HAVE_TOMLC99)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_TOMLC99)
    target_link_libraries(razorforge_runtime rf_tomlc99)
endif()

# Link minicsv if available
if(HAVE_MINICSV)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_MINICSV)
    target_link_libraries(razorforge_runtime rf_minicsv)
endif()

# Link libco if available
if(HAVE_LIBCO)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_LIBCO)
    target_link_libraries(razorforge_runtime rf_libco)
endif()

# Link libuv if available
if(HAVE_LIBUV)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_LIBUV)
    target_link_libraries(razorforge_runtime uv_a)
endif()


# Set output directory
set_target_properties(razorforge_runtime PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
    ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
)

# ============================================================================
# Math library (standalone, for static linking)
# ============================================================================
add_library(razorforge_math STATIC
    runtime/math_functions.c
    runtime/rf_divide.c
)

target_include_directories(razorforge_math PUBLIC include)

if(UNIX AND NOT APPLE)
    target_link_libraries(razorforge_math m)
endif()

# On Windows, link the compiler-rt shim for 128-bit integer operations
if(WIN32)
    target_link_libraries(razorforge_math compiler_rt_shim)
endif()

set_target_properties(razorforge_math PROPERTIES
    ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib
)

# ============================================================================
# Install targets
# ============================================================================
install(TARGETS razorforge_runtime razorforge_math
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)

install(FILES include/razorforge_math.h
    DESTINATION include
)

install(FILES include/razorforge_runtime.h
    DESTINATION include
)

# Print configuration summary
message(STATUS "")
message(STATUS "RazorForge Native Configuration:")
message(STATUS "  ── Modules ──")
message(STATUS "  zlib (compression):               ${HAVE_ZLIB}")
message(STATUS "  zstd (compression):               ${HAVE_ZSTD}")
message(STATUS "  sqlite3 (database):               ${HAVE_SQLITE3}")
message(STATUS "  libsodium (crypto):               ${HAVE_LIBSODIUM}")
message(STATUS "  mbedTLS (TLS/crypto):             ${HAVE_MBEDTLS}")
message(STATUS "  PCRE2 (regex):                    ${HAVE_PCRE2}")
message(STATUS "  utf8proc (unicode):               ${HAVE_UTF8PROC}")
message(STATUS "  yyjson (JSON):                    ${HAVE_YYJSON}")
message(STATUS "  tomlc99 (TOML):                   ${HAVE_TOMLC99}")
message(STATUS "  minicsv (CSV):                    ${HAVE_MINICSV}")
message(STATUS "  libco (green-thread contexts):    ${HAVE_LIBCO}")
message(STATUS "  libuv (async I/O loop):           ${HAVE_LIBUV}")
message(STATUS "  ── Platform ──")
message(STATUS "  Platform: ${CMAKE_SYSTEM_NAME}")
message(STATUS "  Compiler: ${CMAKE_C_COMPILER_ID} ${CMAKE_C_COMPILER_VERSION}")
message(STATUS "")
