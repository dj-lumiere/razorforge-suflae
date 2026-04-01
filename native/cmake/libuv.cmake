# libuv - Cross-platform async I/O and event loop
# https://github.com/libuv/libuv
# License: MIT
#
# Provides: event loop, async I/O polling, timers, work queue integration
# Used by: RazorForge/Suflae async runtime backend
#
# To install:
#   git clone --depth 1 https://github.com/libuv/libuv.git native/libuv

set(LIBUV_DIR "${CMAKE_CURRENT_SOURCE_DIR}/libuv")
set(LIBUV_INCLUDE_DIR "${LIBUV_DIR}/include")
set(LIBUV_SRC_DIR "${LIBUV_DIR}/src")
option(RAZORFORGE_BUILD_VENDOR_LIBUV "Build vendored libuv sources into uv_a" OFF)

if(EXISTS "${LIBUV_INCLUDE_DIR}/uv.h")
    message(STATUS "Found libuv")

    if(RAZORFORGE_BUILD_VENDOR_LIBUV)
        set(LIBUV_SOURCES
            ${LIBUV_SRC_DIR}/fs-poll.c
            ${LIBUV_SRC_DIR}/idna.c
            ${LIBUV_SRC_DIR}/inet.c
            ${LIBUV_SRC_DIR}/random.c
            ${LIBUV_SRC_DIR}/strscpy.c
            ${LIBUV_SRC_DIR}/strtok.c
            ${LIBUV_SRC_DIR}/thread-common.c
            ${LIBUV_SRC_DIR}/threadpool.c
            ${LIBUV_SRC_DIR}/timer.c
            ${LIBUV_SRC_DIR}/uv-common.c
            ${LIBUV_SRC_DIR}/uv-data-getter-setters.c
            ${LIBUV_SRC_DIR}/version.c
        )

        set(LIBUV_DEFINES "")
        set(LIBUV_LINK_LIBS "")

        if(WIN32)
            list(APPEND LIBUV_DEFINES WIN32_LEAN_AND_MEAN _WIN32_WINNT=0x0A00 _CRT_DECLARE_NONSTDC_NAMES=0 _CRT_SECURE_NO_WARNINGS)
            list(APPEND LIBUV_LINK_LIBS
                psapi
                user32
                advapi32
                iphlpapi
                userenv
                ws2_32
                dbghelp
                ole32
                shell32
            )
            list(APPEND LIBUV_SOURCES
                ${LIBUV_SRC_DIR}/win/async.c
                ${LIBUV_SRC_DIR}/win/core.c
                ${LIBUV_SRC_DIR}/win/detect-wakeup.c
                ${LIBUV_SRC_DIR}/win/dl.c
                ${LIBUV_SRC_DIR}/win/error.c
                ${LIBUV_SRC_DIR}/win/fs.c
                ${LIBUV_SRC_DIR}/win/fs-event.c
                ${LIBUV_SRC_DIR}/win/getaddrinfo.c
                ${LIBUV_SRC_DIR}/win/getnameinfo.c
                ${LIBUV_SRC_DIR}/win/handle.c
                ${LIBUV_SRC_DIR}/win/loop-watcher.c
                ${LIBUV_SRC_DIR}/win/pipe.c
                ${LIBUV_SRC_DIR}/win/thread.c
                ${LIBUV_SRC_DIR}/win/poll.c
                ${LIBUV_SRC_DIR}/win/process.c
                ${LIBUV_SRC_DIR}/win/process-stdio.c
                ${LIBUV_SRC_DIR}/win/signal.c
                ${LIBUV_SRC_DIR}/win/snprintf.c
                ${LIBUV_SRC_DIR}/win/stream.c
                ${LIBUV_SRC_DIR}/win/tcp.c
                ${LIBUV_SRC_DIR}/win/tty.c
                ${LIBUV_SRC_DIR}/win/udp.c
                ${LIBUV_SRC_DIR}/win/util.c
                ${LIBUV_SRC_DIR}/win/winapi.c
                ${LIBUV_SRC_DIR}/win/winsock.c
            )
        else()
            list(APPEND LIBUV_DEFINES _FILE_OFFSET_BITS=64 _LARGEFILE_SOURCE)
            if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
                list(APPEND LIBUV_DEFINES _GNU_SOURCE _POSIX_C_SOURCE=200112)
                list(APPEND LIBUV_LINK_LIBS pthread dl rt m)
                list(APPEND LIBUV_SOURCES
                    ${LIBUV_SRC_DIR}/unix/async.c
                    ${LIBUV_SRC_DIR}/unix/core.c
                    ${LIBUV_SRC_DIR}/unix/dl.c
                    ${LIBUV_SRC_DIR}/unix/fs.c
                    ${LIBUV_SRC_DIR}/unix/getaddrinfo.c
                    ${LIBUV_SRC_DIR}/unix/getnameinfo.c
                    ${LIBUV_SRC_DIR}/unix/loop-watcher.c
                    ${LIBUV_SRC_DIR}/unix/loop.c
                    ${LIBUV_SRC_DIR}/unix/pipe.c
                    ${LIBUV_SRC_DIR}/unix/poll.c
                    ${LIBUV_SRC_DIR}/unix/process.c
                    ${LIBUV_SRC_DIR}/unix/random-devurandom.c
                    ${LIBUV_SRC_DIR}/unix/signal.c
                    ${LIBUV_SRC_DIR}/unix/stream.c
                    ${LIBUV_SRC_DIR}/unix/tcp.c
                    ${LIBUV_SRC_DIR}/unix/thread.c
                    ${LIBUV_SRC_DIR}/unix/tty.c
                    ${LIBUV_SRC_DIR}/unix/udp.c
                    ${LIBUV_SRC_DIR}/unix/proctitle.c
                    ${LIBUV_SRC_DIR}/unix/linux.c
                    ${LIBUV_SRC_DIR}/unix/procfs-exepath.c
                    ${LIBUV_SRC_DIR}/unix/random-getrandom.c
                    ${LIBUV_SRC_DIR}/unix/random-sysctl-linux.c
                )
            elseif(APPLE)
                list(APPEND LIBUV_DEFINES _FILE_OFFSET_BITS=64 _LARGEFILE_SOURCE _DARWIN_UNLIMITED_SELECT=1 _DARWIN_USE_64_BIT_INODE=1)
                list(APPEND LIBUV_LINK_LIBS pthread m)
                list(APPEND LIBUV_SOURCES
                    ${LIBUV_SRC_DIR}/unix/async.c
                    ${LIBUV_SRC_DIR}/unix/core.c
                    ${LIBUV_SRC_DIR}/unix/dl.c
                    ${LIBUV_SRC_DIR}/unix/fs.c
                    ${LIBUV_SRC_DIR}/unix/getaddrinfo.c
                    ${LIBUV_SRC_DIR}/unix/getnameinfo.c
                    ${LIBUV_SRC_DIR}/unix/loop-watcher.c
                    ${LIBUV_SRC_DIR}/unix/loop.c
                    ${LIBUV_SRC_DIR}/unix/pipe.c
                    ${LIBUV_SRC_DIR}/unix/poll.c
                    ${LIBUV_SRC_DIR}/unix/process.c
                    ${LIBUV_SRC_DIR}/unix/random-devurandom.c
                    ${LIBUV_SRC_DIR}/unix/signal.c
                    ${LIBUV_SRC_DIR}/unix/stream.c
                    ${LIBUV_SRC_DIR}/unix/tcp.c
                    ${LIBUV_SRC_DIR}/unix/thread.c
                    ${LIBUV_SRC_DIR}/unix/tty.c
                    ${LIBUV_SRC_DIR}/unix/udp.c
                    ${LIBUV_SRC_DIR}/unix/proctitle.c
                    ${LIBUV_SRC_DIR}/unix/bsd-ifaddrs.c
                    ${LIBUV_SRC_DIR}/unix/kqueue.c
                    ${LIBUV_SRC_DIR}/unix/random-getentropy.c
                    ${LIBUV_SRC_DIR}/unix/darwin-proctitle.c
                    ${LIBUV_SRC_DIR}/unix/darwin.c
                    ${LIBUV_SRC_DIR}/unix/fsevents.c
                )
            else()
                message(STATUS "libuv vendored, but manual RazorForge integration only covers Windows/Linux/macOS right now")
            endif()
        endif()

        add_library(uv_a STATIC ${LIBUV_SOURCES})
        target_include_directories(uv_a PUBLIC ${LIBUV_INCLUDE_DIR} PRIVATE ${LIBUV_SRC_DIR})
        target_compile_definitions(uv_a PRIVATE ${LIBUV_DEFINES})

        if(CMAKE_C_COMPILER_ID STREQUAL "Clang" OR CMAKE_C_COMPILER_ID STREQUAL "GNU")
            target_compile_options(uv_a PRIVATE -Wno-unused-parameter)
            if(NOT WIN32)
                target_compile_options(uv_a PRIVATE -fPIC)
            endif()
        elseif(MSVC)
            target_compile_options(uv_a PRIVATE /W0)
        endif()

        target_link_libraries(uv_a ${LIBUV_LINK_LIBS})

        if(WIN32)
            set_target_properties(uv_a PROPERTIES PREFIX "lib")
        endif()
    else()
        add_library(uv_a INTERFACE)
        target_include_directories(uv_a INTERFACE ${LIBUV_INCLUDE_DIR})
        message(STATUS "libuv build deferred (RAZORFORGE_BUILD_VENDOR_LIBUV=OFF)")
    endif()

    set(HAVE_LIBUV TRUE)
else()
    add_library(uv_a INTERFACE)
    set(HAVE_LIBUV FALSE)

    message(STATUS "")
    message(STATUS "libuv not found. To enable async I/O runtime support:")
    message(STATUS "  git clone --depth 1 https://github.com/libuv/libuv.git native/libuv")
    message(STATUS "")
endif()
