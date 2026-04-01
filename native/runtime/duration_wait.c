// duration_wait.c - Blocking wait for duration literals

#include "types.h"

#ifdef _WIN32
#include <windows.h>
#else
#include <errno.h>
#include <time.h>
#endif

void rf_waitfor_duration(rf_S64 duration_seconds, rf_U32 duration_nanoseconds)
{
    if (duration_seconds < 0 || (duration_seconds == 0 && duration_nanoseconds == 0))
    {
        return;
    }

#ifdef _WIN32
    unsigned long long total_ms =
        (unsigned long long)duration_seconds * 1000ULL +
        ((unsigned long long)duration_nanoseconds + 999999ULL) / 1000000ULL;

    if (total_ms > 0xFFFFFFFFULL)
    {
        total_ms = 0xFFFFFFFFULL;
    }

    Sleep((DWORD)total_ms);
#else
    struct timespec req;
    req.tv_sec = (time_t)duration_seconds;
    req.tv_nsec = (long)duration_nanoseconds;

    while (nanosleep(&req, &req) == -1 && errno == EINTR)
    {
    }
#endif
}
