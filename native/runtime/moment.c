/*
 * RazorForge Runtime - Moment
 * Native implementation of Moment.now()
 */

#include "types.h"

#ifdef _WIN32
#include <windows.h>
#else
#include <time.h>
#endif

// Moment record layout: { S64 seconds, U32 nanoseconds }
typedef struct rf_Moment {
    rf_s64 seconds;
    rf_u32 nanoseconds;
} rf_Moment;

// Get current time as seconds since Unix epoch + nanoseconds
rf_Moment rf_moment_now()
{
    rf_Moment result;

#ifdef _WIN32
    // FILETIME: 100-nanosecond intervals since 1601-01-01
    // Unix epoch offset: 116444736000000000 (in 100ns units)
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);

    ULARGE_INTEGER uli;
    uli.LowPart = ft.dwLowMoment;
    uli.HighPart = ft.dwHighMoment;

    // Convert to Unix epoch (subtract offset)
    const uint64_t EPOCH_OFFSET = 116444736000000000ULL;
    uint64_t ticks = uli.QuadPart - EPOCH_OFFSET;

    // Split into seconds and nanoseconds
    result.seconds = (rf_s64)(ticks / 10000000ULL);
    result.nanoseconds = (rf_u32)((ticks % 10000000ULL) * 100ULL);
#else
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);

    result.seconds = (rf_s64)ts.tv_sec;
    result.nanoseconds = (rf_u32)ts.tv_nsec;
#endif

    return result;
}