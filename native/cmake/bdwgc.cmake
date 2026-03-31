# Boehm-Demers-Weiser Garbage Collector (bdwgc)
# https://github.com/ivmai/bdwgc
# License: MIT-style
#
# Provides: conservative garbage collector for C/C++
# Used by: Suflae runtime (GC for actor model)
#
# To install:
#   git clone --depth 1 https://github.com/ivmai/bdwgc.git native/bdwgc

set(BDWGC_DIR "${CMAKE_CURRENT_SOURCE_DIR}/bdwgc")

if(EXISTS "${BDWGC_DIR}/CMakeLists.txt" AND EXISTS "${BDWGC_DIR}/include/gc.h")
    message(STATUS "Found Boehm GC")

    set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
    set(build_tests OFF CACHE BOOL "" FORCE)
    set(enable_cplusplus OFF CACHE BOOL "" FORCE)
    set(enable_docs OFF CACHE BOOL "" FORCE)
    add_subdirectory(${BDWGC_DIR} ${CMAKE_BINARY_DIR}/bdwgc EXCLUDE_FROM_ALL)

    set(HAVE_BDWGC TRUE)
else()
    add_library(rf_gc INTERFACE)
    set(HAVE_BDWGC FALSE)

    message(STATUS "")
    message(STATUS "Boehm GC not found. To enable Suflae garbage collector:")
    message(STATUS "  git clone --depth 1 https://github.com/ivmai/bdwgc.git native/bdwgc")
    message(STATUS "")
endif()