# TLFloat - IEEE 754 quad-precision soft-float with correctly-rounded
# arithmetic and a full libm-equivalent function set (sin/cos/exp/pow/...).
# https://github.com/shibatch/tlfloat (Boost Software License 1.0)
#
# Backs all F128 runtime functions (f128_functions.c) and, through the f128
# bridge, the D32/D64/D128 transcendentals. Pure soft-float: results are
# bit-identical on every platform, which the fixture snapshots rely on.
#
# TLFloat is C++20 internally but exposes a plain C API (tlfloat/tlfloat.h);
# the runtime stays C and only calls the C API.
#
# To install:
#   git clone --depth 1 https://github.com/shibatch/tlfloat.git native/tlfloat

set(TLFLOAT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/tlfloat")

if(EXISTS "${TLFLOAT_DIR}/src/include/tlfloat/tlfloat.h")
    message(STATUS "Found TLFloat")

    # TLFloat declares these with option(); with CMP0077 NEW behavior the
    # normal variables below take precedence. Unset after add_subdirectory so
    # the generic names don't leak into later vendored subprojects.
    set(BUILD_LIBS ON)
    set(BUILD_TESTS OFF)
    set(BUILD_UTILS OFF)
    set(BUILD_BENCH OFF)

    # The static lib is linked into the shared razorforge_runtime.
    set(SAVED_PIC ${CMAKE_POSITION_INDEPENDENT_CODE})
    set(CMAKE_POSITION_INDEPENDENT_CODE ON)

    add_subdirectory("${TLFLOAT_DIR}" "${CMAKE_BINARY_DIR}/tlfloat")

    set(CMAKE_POSITION_INDEPENDENT_CODE ${SAVED_PIC})
    unset(BUILD_LIBS)
    unset(BUILD_TESTS)
    unset(BUILD_UTILS)
    unset(BUILD_BENCH)

    # TLFloat sets include paths with directory-scoped include_directories()
    # only, so they don't propagate to linkers of the `tlfloat` target. Wrap
    # it in an INTERFACE target carrying the source headers plus the
    # generated tlfloatconfig.hpp from the subproject binary dir.
    add_library(rf_tlfloat INTERFACE)
    target_include_directories(rf_tlfloat INTERFACE
        "${TLFLOAT_DIR}/src/include"
        "${CMAKE_BINARY_DIR}/tlfloat/include"
    )
    target_link_libraries(rf_tlfloat INTERFACE tlfloat)

    set(HAVE_TLFLOAT TRUE)
else()
    add_library(rf_tlfloat INTERFACE)
    set(HAVE_TLFLOAT FALSE)

    message(STATUS "")
    message(STATUS "TLFloat not found. F128 math and decimal transcendentals need it:")
    message(STATUS "  git clone --depth 1 https://github.com/shibatch/tlfloat.git native/tlfloat")
    message(STATUS "")
endif()
