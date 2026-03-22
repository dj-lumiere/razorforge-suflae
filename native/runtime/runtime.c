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
rf_UAddr rf_strlen(const char* cstr)
{
    return (rf_UAddr)strlen(cstr);
}
rf_UAddr rf_strcpy(char* dest, const char* src)
{
    return (rf_UAddr)strcpy(dest, src);
}
rf_S32 rf_strcmp(const char* s1, const char* s2)
{
    return (rf_S32)strcmp(s1, s2);
}

// ============================================================================
// Text Entity Construction (UTF-8 CStr → UTF-32 Text)
// ============================================================================

// Text entity layout:   { ptr letters }
// List[Letter] layout:  { ptr data, i64 count, i64 capacity }
// data points to array of i32 (UTF-32 codepoints)
void* rf_text_from_cstr(const char* cstr)
{
    if (!cstr) cstr = "";
    size_t len = strlen(cstr);

    // Count UTF-32 codepoints
    size_t cp_count = 0;
    for (size_t i = 0; i < len; ) {
        uint8_t b = (uint8_t)cstr[i];
        if (b < 0x80) i += 1;
        else if (b < 0xE0) i += 2;
        else if (b < 0xF0) i += 3;
        else i += 4;
        cp_count++;
    }

    // Allocate codepoint data
    uint32_t* data = (uint32_t*)malloc(cp_count * sizeof(uint32_t));
    if (!data) { fprintf(stderr, "RazorForge: OOM in rf_text_from_cstr\n"); abort(); }

    // Decode UTF-8 → UTF-32
    size_t idx = 0;
    for (size_t i = 0; i < len; ) {
        uint8_t b0 = (uint8_t)cstr[i];
        uint32_t cp;
        if (b0 < 0x80)       { cp = b0; i += 1; }
        else if (b0 < 0xE0)  { cp = ((b0 & 0x1F) << 6)  | (cstr[i+1] & 0x3F); i += 2; }
        else if (b0 < 0xF0)  { cp = ((b0 & 0x0F) << 12) | ((cstr[i+1] & 0x3F) << 6) | (cstr[i+2] & 0x3F); i += 3; }
        else                  { cp = ((b0 & 0x07) << 18) | ((cstr[i+1] & 0x3F) << 12) | ((cstr[i+2] & 0x3F) << 6) | (cstr[i+3] & 0x3F); i += 4; }
        data[idx++] = cp;
    }

    // Allocate List[Letter] entity: { ptr data, i64 count, i64 capacity }
    typedef struct { void* data; int64_t count; int64_t capacity; } ListLetter;
    ListLetter* list = (ListLetter*)malloc(sizeof(ListLetter));
    if (!list) { fprintf(stderr, "RazorForge: OOM in rf_text_from_cstr\n"); abort(); }
    list->data = data;
    list->count = (int64_t)cp_count;
    list->capacity = (int64_t)cp_count;

    // Allocate Text entity: { ptr letters }
    typedef struct { void* letters; } TextEntity;
    TextEntity* text = (TextEntity*)malloc(sizeof(TextEntity));
    if (!text) { fprintf(stderr, "RazorForge: OOM in rf_text_from_cstr\n"); abort(); }
    text->letters = list;

    return text;
}

// ============================================================================
// Console I/O Operations
// ============================================================================

// Count-based print to stdout (preferred - no null-termination required)
void rf_console_print(const char* ptr, rf_UAddr count) { fwrite(ptr, 1, count, stdout); }
void rf_console_print_line(const char* ptr, rf_UAddr count) { fwrite(ptr, 1, count, stdout); putchar('\n'); }

// Count-based print to stderr (preferred - no null-termination required)
void rf_console_alert(const char* ptr, rf_UAddr count) { fwrite(ptr, 1, count, stderr); }
void rf_console_alert_line(const char* ptr, rf_UAddr count) { fwrite(ptr, 1, count, stderr); fputc('\n', stderr); }

// Print empty line
void rf_console_print_newline() { putchar('\n'); }
void rf_console_alert_newline() { fputc('\n', stderr); }

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
char* rf_console_get_letters(rf_UAddr count)
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

// Print null-terminated string to stdout (no newline appended)
void rf_console_puts(const char* str) { fputs(str, stdout); }

// Print null-terminated string to stderr (no newline appended)
void rf_console_alert_puts(const char* str) { fputs(str, stderr); }

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
