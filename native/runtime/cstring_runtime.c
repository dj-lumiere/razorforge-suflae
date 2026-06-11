// cstring_runtime.c - C string helpers exposed to RazorForge

#include "types.h"

#include <string.h>
#include <wchar.h>

rf_address rf_cstr_count(const char* cstr)
{
    return (rf_address)strlen(cstr);
}

char* rf_cstr_copy(char* dest, const char* src)
{
    return strcpy(dest, src);
}

rf_S32 rf_cstr_compare(const char* s1, const char* s2)
{
    return (rf_S32)strcmp(s1, s2);
}

rf_address rf_cwstr_count(const wchar_t* cwstr)
{
    return (rf_address)wcslen(cwstr);
}

rf_S32 rf_cwstr_compare(const wchar_t* s1, const wchar_t* s2)
{
    return (rf_S32)wcscmp(s1, s2);
}
