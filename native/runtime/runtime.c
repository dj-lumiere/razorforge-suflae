// runtime.c - Minimal operations for v0.0.1

// Memory operations
#ifdef _WIN32
#include <windows.h>
#endif
#include "types.h"
#include <string.h>

void rf_runtime_init()
{
#ifdef _WIN32
    SetConsoleCP(65001);
    SetConsoleOutputCP(65001);
#endif
}

// ============================================================================
// DynamicSlice operations (memory functions are in memory.c)
// ============================================================================

// ============================================================================
// C String Helpers
// ============================================================================

// Get length of cstr
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

// ============================================================================
// Console I/O Operations
// ============================================================================

// Count-based print to stdout (preferred - no null-termination required)
void rf_console_show(const char* ptr, rf_address count) { fwrite(ptr, 1, count, stdout); }

// Count-based print to stderr (preferred - no null-termination required)
void rf_console_alert(const char* ptr, rf_address count) { fwrite(ptr, 1, count, stderr); }

// Input operations - get single character
char rf_console_get_char()
{
    return getchar();
}

// Input operations - get line (allocates buffer, returns null-terminated string)
char* rf_console_get_line()
{
    fflush(stdout);
    int c;
    // Skip leading line feeds first
    while ((c = getchar()) != EOF && (c == '\n' || c == '\r'))
    {
    }

    if (c == EOF) return NULL;

    size_t capacity = 256;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer) return NULL;
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

    // Handle EOF with no input
    if (c == EOF && length == 0)
    {
        free(buffer);
        return NULL;
    }

    return buffer;
}

// Input operations - get word (whitespace-delimited)
char* rf_console_get_word()
{
    fflush(stdout);
    int c;
    // Skip leading whitespaces first
    while ((c = getchar()) != EOF && (c == ' ' || c == '\t' || c == '\n' || c == '\r'))
    {
    }

    if (c == EOF) { return NULL; }

    size_t capacity = 256;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer) { return NULL; }
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

    // Handle EOF with no input
    if (c == EOF && length == 0)
    {
        free(buffer);
        return NULL;
    }

    return buffer;
}

// Input operations - get exactly n characters
char* rf_console_get_letters(rf_address count)
{
    fflush(stdout);
    char* buffer = malloc(count + 1);
    if (!buffer) return NULL;

    size_t read = fread(buffer, 1, count, stdin);
    buffer[read] = '\0';
    return buffer;
}

// Input operations - get all input until EOF
char* rf_console_get_all()
{
    fflush(stdout);
    size_t capacity = 1024;
    size_t length = 0;
    char* buffer = malloc(capacity);
    if (!buffer) return NULL;

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

// Flush output
void rf_console_flush()
{
    fflush(stdout);
}

// Clear screen (platform-specific)
void rf_console_clear()
{
#ifdef _WIN32
    system("cls");
#else
    system("clear");
#endif
}

// ============================================================================
// Stack Trace + Crash Reporting
// ============================================================================

#define RF_TRACE_MAX 10

typedef struct {
    const char* routine;
    const char* file;
    int32_t line, col;
} rf_TraceFrame;

static rf_TraceFrame rf_trace_stack[RF_TRACE_MAX];
static int32_t rf_trace_depth = 0;

void rf_trace_push(const char* routine, const char* file, int32_t line, int32_t col)
{
    if (rf_trace_depth < RF_TRACE_MAX)
        rf_trace_stack[rf_trace_depth] = (rf_TraceFrame){routine, file, line, col};
    rf_trace_depth++;
}

void rf_trace_pop(void)
{
    if (rf_trace_depth > 0) rf_trace_depth--;
}

void rf_crash(const char* type_name, int64_t type_len,
              const char* file, int64_t file_len,
              int32_t line, int32_t col,
              const int32_t* message_utf32, int64_t message_len)
{
    fprintf(stderr, "%.*s: ", (int)type_len, type_name);

    if (message_utf32 != NULL && message_len > 0)
    {
        for (int64_t i = 0; i < message_len; i++)
        {
            int32_t cp = message_utf32[i];
            if (cp <= 0x7F) fputc(cp, stderr);
            else if (cp <= 0x7FF) { fputc(0xC0 | (cp >> 6), stderr); fputc(0x80 | (cp & 0x3F), stderr); }
            else if (cp <= 0xFFFF) { fputc(0xE0 | (cp >> 12), stderr); fputc(0x80 | ((cp >> 6) & 0x3F), stderr); fputc(0x80 | (cp & 0x3F), stderr); }
            else { fputc(0xF0 | (cp >> 18), stderr); fputc(0x80 | ((cp >> 12) & 0x3F), stderr); fputc(0x80 | ((cp >> 6) & 0x3F), stderr); fputc(0x80 | (cp & 0x3F), stderr); }
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
                frames - 1 - i, rf_trace_stack[i].routine,
                rf_trace_stack[i].file, rf_trace_stack[i].line, rf_trace_stack[i].col);
        }
        if (rf_trace_depth > RF_TRACE_MAX)
            fprintf(stderr, "  ... %d more frames\n", rf_trace_depth - RF_TRACE_MAX);
    }

    exit(1);
}

// ============================================================================
// Threading Primitives for Shared<T, Policy>
// ============================================================================
// These are placeholder implementations for single-threaded use.
// Full multi-threaded support requires platform-specific threading libraries.

#ifdef _WIN32
#include <windows.h>
#else
#include <pthread.h>
#endif

// Shared<T, Policy> structure (simplified)
// In real implementation, this would be more complex with proper memory layout
typedef struct
{
    void* data; // Pointer to inner data
#ifdef _WIN32
    CRITICAL_SECTION mutex;
    SRWLOCK rwlock;
#else
    pthread_mutex_t mutex;
    pthread_rwlock_t rwlock;
#endif
    rf_U32 ref_count; // Reference count (atomic in real impl)
} rf_Shared;

// Mutex lock - acquire exclusive access
void* razorforge_mutex_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    EnterCriticalSection(&shared->mutex);
#else
    pthread_mutex_lock(&shared->mutex);
#endif
    return shared->data;
}

// Mutex unlock - release exclusive access
void razorforge_mutex_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    LeaveCriticalSection(&shared->mutex);
#else
    pthread_mutex_unlock(&shared->mutex);
#endif
}

// RwLock read lock - acquire shared read access
void* razorforge_rwlock_read_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    AcquireSRWLockShared(&shared->rwlock);
#else
    pthread_rwlock_rdlock(&shared->rwlock);
#endif
    return shared->data;
}

// RwLock read unlock - release shared read access
void razorforge_rwlock_read_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    ReleaseSRWLockShared(&shared->rwlock);
#else
    pthread_rwlock_unlock(&shared->rwlock);
#endif
}

// RwLock write lock - acquire exclusive write access
void* razorforge_rwlock_write_lock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    AcquireSRWLockExclusive(&shared->rwlock);
#else
    pthread_rwlock_wrlock(&shared->rwlock);
#endif
    return shared->data;
}

// RwLock write unlock - release exclusive write access
void razorforge_rwlock_write_unlock(void* shared_ptr)
{
    rf_Shared* shared = (rf_Shared*)shared_ptr;
#ifdef _WIN32
    ReleaseSRWLockExclusive(&shared->rwlock);
#else
    pthread_rwlock_unlock(&shared->rwlock);
#endif
}
