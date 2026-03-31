# ICU - International Components for Unicode
# https://github.com/unicode-org/icu
# License: Unicode License
#
# Provides: comprehensive Unicode support, collation, formatting, etc.
# Used by: Unicode module (optional — comprehensive Unicode beyond core Text)
#
# NOTE: ICU has its own complex build system (autotools/MSVC projects).
# It cannot be easily built as a CMake subdirectory.
# This file checks for pre-built or system-installed ICU.
#
# To install:
#   - Windows: vcpkg install icu  OR  download prebuilt from https://icu.unicode.org/
#   - Linux:   apt install libicu-dev
#   - macOS:   brew install icu4c

# Try to find system ICU
find_package(ICU QUIET COMPONENTS uc i18n data)

if(ICU_FOUND)
    message(STATUS "Found ICU ${ICU_VERSION} (system)")
    set(HAVE_ICU TRUE)
else()
    set(HAVE_ICU FALSE)
    message(STATUS "")
    message(STATUS "ICU not found. ICU requires its own build system.")
    message(STATUS "  Windows: vcpkg install icu")
    message(STATUS "  Linux:   apt install libicu-dev")
    message(STATUS "  macOS:   brew install icu4c")
    message(STATUS "")
endif()