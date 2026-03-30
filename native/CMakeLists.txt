cmake_minimum_required(VERSION 3.20)
project(RazorForgeNative C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)

# Export all symbols on Windows (generates .lib import library)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS ON)

# Options
option(ENABLE_INTELDECIMAL "Enable IEEE 754 decimal floating point support via Intel DFP" ON)

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
# Third-party libraries (sources are gitignored, download separately)
# Build configurations are in cmake/*.cmake (tracked in git)
# ============================================================================

# LibBF - Arbitrary precision floating point with transcendentals
# https://bellard.org/libbf/ (MIT)
include(cmake/libbf.cmake)

# Intel Decimal Floating-Point Math Library (libbid)
# https://www.intel.com/content/www/us/en/developer/articles/tool/intel-decimal-floating-point-math-library.html (BSD-3-Clause)
include(cmake/inteldecimal.cmake)

# LibTomMath - Arbitrary precision integer arithmetic
# https://github.com/libtom/libtommath (Public Domain)
include(cmake/libtommath.cmake)

# MAPM - Arbitrary precision decimal arithmetic
# https://github.com/LuaDist/mapm (Freeware)
include(cmake/mapm.cmake)

# ============================================================================
# Compiler-RT shim for 128-bit integer operations (Windows)
# LibBF uses __int128 which requires __udivti3 etc. from compiler-rt/libgcc
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
    runtime/runtime.c
    runtime/memory.c
    runtime/stacktrace.c
    runtime/math_functions.c
    runtime/decimal_functions.c
    runtime/bignum_functions.c
    runtime/f16_functions.c
    runtime/f128_functions.c
    runtime/text_functions.c
    runtime/csharp_interop.c
    runtime/file_functions.c
    runtime/types.h
)

target_include_directories(razorforge_runtime PUBLIC include)

# Link math library
if(UNIX AND NOT APPLE)
    target_link_libraries(razorforge_runtime m)
endif()

# Link LibBF (f128 support with transcendentals)
if(HAVE_LIBBF)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_LIBBF)
    target_link_libraries(razorforge_runtime libbf)
    # On Windows, link the compiler-rt shim for 128-bit integer operations
    if(WIN32)
        target_link_libraries(razorforge_runtime compiler_rt_shim)
    endif()
endif()

# Link Intel Decimal if available
if(HAVE_INTELDECIMAL)
    target_compile_definitions(razorforge_runtime PRIVATE
        HAVE_INTELDECIMAL
        DECIMAL_CALL_BY_REFERENCE=0
        DECIMAL_GLOBAL_ROUNDING=1
        DECIMAL_GLOBAL_EXCEPTION_FLAGS=1
    )
    target_link_libraries(razorforge_runtime inteldecimal)
endif()

# Link LibTomMath if available
if(HAVE_LIBTOMMATH)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_LIBTOMMATH)
    target_link_libraries(razorforge_runtime tommath)
endif()

# Link MAPM if available
if(HAVE_MAPM)
    target_compile_definitions(razorforge_runtime PRIVATE HAVE_MAPM)
    target_link_libraries(razorforge_runtime mapm)
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
    runtime/decimal_functions.c
    runtime/bignum_functions.c
    runtime/f16_functions.c
    runtime/f128_functions.c
)

target_include_directories(razorforge_math PUBLIC include)

if(UNIX AND NOT APPLE)
    target_link_libraries(razorforge_math m)
endif()

if(HAVE_LIBBF)
    target_compile_definitions(razorforge_math PRIVATE HAVE_LIBBF)
    target_link_libraries(razorforge_math libbf)
    # On Windows, link the compiler-rt shim for 128-bit integer operations
    if(WIN32)
        target_link_libraries(razorforge_math compiler_rt_shim)
    endif()
endif()

if(HAVE_INTELDECIMAL)
    target_compile_definitions(razorforge_math PRIVATE
        HAVE_INTELDECIMAL
        DECIMAL_CALL_BY_REFERENCE=0
        DECIMAL_GLOBAL_ROUNDING=1
        DECIMAL_GLOBAL_EXCEPTION_FLAGS=1
    )
    target_link_libraries(razorforge_math inteldecimal)
endif()

if(HAVE_LIBTOMMATH)
    target_compile_definitions(razorforge_math PRIVATE HAVE_LIBTOMMATH)
    target_link_libraries(razorforge_math tommath)
endif()

if(HAVE_MAPM)
    target_compile_definitions(razorforge_math PRIVATE HAVE_MAPM)
    target_link_libraries(razorforge_math mapm)
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

# Print configuration summary
message(STATUS "")
message(STATUS "RazorForge Native Configuration:")
message(STATUS "  LibBF (f128 + transcendentals): ${HAVE_LIBBF}")
message(STATUS "  Intel Decimal (d32/d64/d128):   ${HAVE_INTELDECIMAL}")
message(STATUS "  LibTomMath (bigint):            ${HAVE_LIBTOMMATH}")
message(STATUS "  MAPM (bigdecimal):              ${HAVE_MAPM}")
message(STATUS "  Platform: ${CMAKE_SYSTEM_NAME}")
message(STATUS "")
