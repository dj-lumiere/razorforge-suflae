/*
 * RazorForge Runtime - Cryptographic Random
 * Produces a single cryptographically-random 64-bit word.
 * Windows: BCryptGenRandom (no extra libs needed).
 * POSIX:   getrandom(2) (Linux 3.17+ / glibc 2.25+, also on macOS via getentropy).
 */

#include "types.h"

#ifdef _WIN32
#include <windows.h>
#include <bcrypt.h>
#pragma comment(lib, "bcrypt.lib")

rf_U64 rf_random_u64(void)
{
    rf_U64 value = 0;
    BCryptGenRandom(NULL, (PUCHAR)&value, sizeof(value), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    return value;
}

#else
#include <sys/random.h>

rf_U64 rf_random_u64(void)
{
    rf_U64 value = 0;
    getrandom(&value, sizeof(value), 0);
    return value;
}
#endif
