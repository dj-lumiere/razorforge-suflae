// crash_runtime.c - Lightweight crash reporting and trace stack

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#define RF_TRACE_MAX 10

typedef struct
{
    const char* routine;
    const char* file;
    int32_t line;
    int32_t col;
} rf_TraceFrame;

static rf_TraceFrame rf_trace_stack[RF_TRACE_MAX];
static int32_t rf_trace_depth = 0;

void rf_trace_push(const char* routine, const char* file, int32_t line, int32_t col)
{
    if (rf_trace_depth < RF_TRACE_MAX)
    {
        rf_trace_stack[rf_trace_depth] = (rf_TraceFrame){ routine, file, line, col };
    }

    rf_trace_depth++;
}

void rf_trace_pop(void)
{
    if (rf_trace_depth > 0)
    {
        rf_trace_depth--;
    }
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

    int frames = rf_trace_depth < RF_TRACE_MAX ? rf_trace_depth : RF_TRACE_MAX;
    if (frames > 0)
    {
        fprintf(stderr, "\nStack trace (most recent first):\n");
        for (int i = frames - 1; i >= 0; i--)
        {
            fprintf(stderr, "  [%d] %s (%s:%d:%d)\n",
                    frames - 1 - i,
                    rf_trace_stack[i].routine,
                    rf_trace_stack[i].file,
                    rf_trace_stack[i].line,
                    rf_trace_stack[i].col);
        }

        if (rf_trace_depth > RF_TRACE_MAX)
        {
            fprintf(stderr, "  ... %d more frames\n", rf_trace_depth - RF_TRACE_MAX);
        }
    }

    fprintf(stderr, "\033[0m");
    exit(1);
}
