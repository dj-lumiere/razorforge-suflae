// console_runtime.c - Console I/O helpers

#include "types.h"

#include <stdio.h>
#include <stdlib.h>

#ifdef _WIN32
#include <io.h>
#define rf_isatty _isatty
#define rf_fileno _fileno
#else
#include <unistd.h>
#define rf_isatty isatty
#define rf_fileno fileno
#endif

// Whether stdout is an interactive terminal. Cached on first use (it never changes
// for the life of the process). When stdout is a TTY we flush after every show() so
// prompts/progress appear immediately; when it is a pipe or file (e.g. fixture capture,
// `buildandrun`, the in-harness runner) we let libc block-buffer instead — flushing per
// call there costs a write() syscall + reader round-trip per print, throttling piped
// output to a fraction of native speed. Buffered output is still delivered: libc flushes
// at exit, every rf_console_ask_* flushes before reading, rf_console_flush() is explicit,
// and rf_crash() flushes stdout before reporting.
static int rf_stdout_is_tty = -1;

static int rf_stdout_interactive(void)
{
    if (rf_stdout_is_tty < 0)
        rf_stdout_is_tty = rf_isatty(rf_fileno(stdout)) ? 1 : 0;
    return rf_stdout_is_tty;
}

void rf_console_show(const char* ptr, rf_address count)
{
    fwrite(ptr, 1, count, stdout);
    if (rf_stdout_interactive())
        fflush(stdout);
}

void rf_console_alert(const char* ptr, rf_address count)
{
    fwrite("\033[95m", 1, 5, stderr);
    fwrite(ptr, 1, count, stderr);
    fwrite("\033[0m", 1, 4, stderr);
    fflush(stderr);
}

char* rf_console_ask_line(void)
{
    fflush(stdout);
    int c;

    while ((c = getchar()) != EOF && (c == '\n' || c == '\r'))
    {
    }

    if (c == EOF)
    {
        return NULL;
    }

    size_t capacity = 256;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer)
    {
        return NULL;
    }

    buffer[length] = (char)c;
    length += 1;

    while ((c = getchar()) != EOF && c != '\n' && c != '\r')
    {
        if (length + 1 >= capacity)
        {
            capacity *= 2;
            char* new_buffer = realloc(buffer, capacity);
            if (!new_buffer)
            {
                free(buffer);
                return NULL;
            }
            buffer = new_buffer;
        }

        buffer[length] = (char)c;
        length += 1;
    }

    buffer[length] = '\0';
    if (c == EOF && length == 0)
    {
        free(buffer);
        return NULL;
    }

    return buffer;
}

char* rf_console_ask_word(void)
{
    fflush(stdout);
    int c;

    while ((c = getchar()) != EOF && (c == ' ' || c == '\t' || c == '\n' || c == '\r'))
    {
    }

    if (c == EOF)
    {
        return NULL;
    }

    size_t capacity = 256;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer)
    {
        return NULL;
    }

    buffer[length] = (char)c;
    length += 1;

    while ((c = getchar()) != EOF && c != '\n' && c != '\r' && c != '\t' && c != ' ')
    {
        if (length + 1 >= capacity)
        {
            capacity *= 2;
            char* new_buffer = realloc(buffer, capacity);
            if (!new_buffer)
            {
                free(buffer);
                return NULL;
            }
            buffer = new_buffer;
        }

        buffer[length] = (char)c;
        length += 1;
    }

    buffer[length] = '\0';
    if (c == EOF && length == 0)
    {
        free(buffer);
        return NULL;
    }

    return buffer;
}

char* rf_console_ask_bytes(rf_address count)
{
    fflush(stdout);
    char* buffer = malloc(count + 1);
    if (!buffer)
    {
        return NULL;
    }

    size_t read = fread(buffer, 1, count, stdin);
    buffer[read] = '\0';
    return buffer;
}

char* rf_console_ask_all(void)
{
    fflush(stdout);
    size_t capacity = 1024;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer)
    {
        return NULL;
    }

    int c;
    while ((c = getchar()) != EOF)
    {
        if (length + 1 >= capacity)
        {
            capacity *= 2;
            char* new_buffer = realloc(buffer, capacity);
            if (!new_buffer)
            {
                free(buffer);
                return NULL;
            }
            buffer = new_buffer;
        }

        buffer[length] = (char)c;
        length += 1;
    }

    buffer[length] = '\0';
    return buffer;
}

void rf_console_flush(void)
{
    fflush(stdout);
}

void rf_console_clear(void)
{
#ifdef _WIN32
    system("cls");
#else
    system("clear");
#endif
}
