// cstring_runtime.c - C string helpers exposed to RazorForge

#include "types.h"

#include <string.h>

rf_address rf_cstr_count(const char* cstr)
{
    return (rf_address)strlen(cstr);
}

rf_address rf_cstr_copy(char* dest, const char* src)
{
    return (rf_address)strcpy(dest, src);
}

rf_S32 rf_cstr_compare(const char* s1, const char* s2)
{
    return (rf_S32)strcmp(s1, s2);
}
