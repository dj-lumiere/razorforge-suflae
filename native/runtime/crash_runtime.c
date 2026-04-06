// crash_runtime.c - Crash reporting with exe-registered shadow stack printer

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#define RF_TRACE_MAX 32

typedef struct
{
    const char* routine;
    const char* file;
    int32_t line;
    int32_t col;
} rf_TraceFrame;

// Stack printer registered by the exe at startup (reads exe-local TLS shadow stack)
static void (*g_stack_printer)(void) = NULL;

void rf_set_stack_printer(void (*fn)(void))
{
    g_stack_printer = fn;
}

// Called by the exe's @_rf_print_trace_stack helper — prints the shadow stack
// data that lives in the exe's TLS globals.
void rf_print_shadow_stack_data(const rf_TraceFrame* stack, int32_t depth)
{
    if (depth <= 0) return;

    // Stack is a ring of RF_TRACE_MAX entries; depth may exceed RF_TRACE_MAX.
    // Print up to RF_TRACE_MAX most-recent frames, most recent first.
    int frames = depth < RF_TRACE_MAX ? (int)depth : RF_TRACE_MAX;
    fprintf(stderr, "Stack trace:\n");
    for (int i = 0; i < frames; i++)
    {
        // Most recent frame is at index (depth - 1) & (RF_TRACE_MAX - 1)
        int idx = (depth - 1 - i) & (RF_TRACE_MAX - 1);
        fprintf(stderr, "  %d: at %s (%s:%d:%d)\n",
                i,
                stack[idx].routine,
                stack[idx].file,
                stack[idx].line,
                stack[idx].col);
    }
    if (depth > RF_TRACE_MAX)
        fprintf(stderr, "  ... (%d frames total)\n", depth);
}

void rf_crash(const char* type_name, int64_t type_len,
              const char* file, int64_t file_len,
              int32_t line, int32_t col,
              const int32_t* message_utf32, int64_t message_len)
{
    fprintf(stderr, "\033[91m%.*s: ", (int)type_len, type_name);

    if (message_utf32 != NULL && message_len > 0)
    {
        for (int64_t i = 0; i < message_len; i++)
        {
            int32_t cp = message_utf32[i];
            if (cp <= 0x7F)
            {
                fputc(cp, stderr);
            }
            else if (cp <= 0x7FF)
            {
                fputc(0xC0 | (cp >> 6), stderr);
                fputc(0x80 | (cp & 0x3F), stderr);
            }
            else if (cp <= 0xFFFF)
            {
                fputc(0xE0 | (cp >> 12), stderr);
                fputc(0x80 | ((cp >> 6) & 0x3F), stderr);
                fputc(0x80 | (cp & 0x3F), stderr);
            }
            else
            {
                fputc(0xF0 | (cp >> 18), stderr);
                fputc(0x80 | ((cp >> 12) & 0x3F), stderr);
                fputc(0x80 | ((cp >> 6) & 0x3F), stderr);
                fputc(0x80 | (cp & 0x3F), stderr);
            }
        }
    }

    fprintf(stderr, "\nat %.*s:%d:%d\n", (int)file_len, file, line, col);

    if (g_stack_printer)
        g_stack_printer();

    fprintf(stderr, "\033[0m");
    fflush(stderr);
    exit(1);
}