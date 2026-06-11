# SQLite3 - Embedded SQL database
# https://www.sqlite.org/
# License: Public Domain
#
# Single-file amalgamation build.
# Used by: Database module
#
# To install:
#   Download amalgamation from https://www.sqlite.org/download.html
#   Extract to native/sqlite3/

set(SQLITE3_DIR "${CMAKE_CURRENT_SOURCE_DIR}/sqlite3")

if(EXISTS "${SQLITE3_DIR}/sqlite3.c" AND EXISTS "${SQLITE3_DIR}/sqlite3.h")
    message(STATUS "Found sqlite3")

    add_library(rf_sqlite3 STATIC
        ${SQLITE3_DIR}/sqlite3.c
    )

    target_include_directories(rf_sqlite3 PUBLIC ${SQLITE3_DIR})

    target_compile_definitions(rf_sqlite3 PRIVATE
        SQLITE_DQS=0
        SQLITE_THREADSAFE=1
        SQLITE_DEFAULT_MEMSTATUS=0
        SQLITE_DEFAULT_WAL_SYNCHRONOUS=1
        SQLITE_LIKE_DOESNT_MATCH_BLOBS
        SQLITE_OMIT_DECLTYPE
        SQLITE_OMIT_DEPRECATED
        SQLITE_OMIT_SHARED_CACHE
        _CRT_SECURE_NO_WARNINGS
    )

    if(APPLE)
        # _POSIX_C_SOURCE puts the macOS SDK headers in strict-POSIX mode,
        # hiding the BSD types (u_short & co), flock()'s LOCK_* constants and
        # the typed-malloc declarations they reference — _DARWIN_C_SOURCE
        # restores the full Darwin surface instead.
        target_compile_definitions(rf_sqlite3 PRIVATE _DARWIN_C_SOURCE=1)
    elseif(NOT WIN32)
        target_compile_definitions(rf_sqlite3 PRIVATE _GNU_SOURCE=1 _POSIX_C_SOURCE=200809L)
    endif()

    if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
        target_compile_options(rf_sqlite3 PRIVATE -w -Wno-implicit-function-declaration)
        if(NOT WIN32)
            target_compile_options(rf_sqlite3 PRIVATE -fPIC)
        endif()
    elseif(MSVC)
        target_compile_options(rf_sqlite3 PRIVATE /W0)
    endif()

    set(HAVE_SQLITE3 TRUE)
else()
    add_library(rf_sqlite3 INTERFACE)
    set(HAVE_SQLITE3 FALSE)

    message(STATUS "")
    message(STATUS "sqlite3 not found. To enable embedded database:")
    message(STATUS "  Download from: https://www.sqlite.org/download.html")
    message(STATUS "  Extract amalgamation to: native/sqlite3/")
    message(STATUS "")
endif()