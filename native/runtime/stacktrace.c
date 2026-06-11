/*
 * RazorForge Runtime - Stack Trace Support
 *
 * Three modes, selected at startup via __rf_set_trace_mode():
 *   RF_TRACE_NONE     (0) - release-time/release-space: no trace, just exit
 *   RF_TRACE_PLATFORM (1) - release: backtrace/RtlCaptureStackBackTrace, function names only
 *   RF_TRACE_SHADOW   (2) - debug: manual push/pop shadow stack, full RF-level detail
 */

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>

// ============================================================================
// Platform detection
// ============================================================================

#ifdef _WIN32
#  define RF_PLATFORM_WINDOWS 1
#  include <windows.h>
#  include <dbghelp.h>
#else
#  define RF_PLATFORM_POSIX 1
#  include <execinfo.h>
#  include <signal.h>
#endif

// ============================================================================
// Trace mode
// ============================================================================

#define RF_TRACE_NONE     0
#define RF_TRACE_PLATFORM 1
#define RF_TRACE_SHADOW   2

static volatile int rf_trace_mode = RF_TRACE_PLATFORM;

// ============================================================================
// Type definitions (shared with codegen layout)
// ============================================================================

typedef uint32_t rf_U32;
typedef uintptr_t rf_address;

typedef struct
{
    rf_U32 file_id;
    rf_U32 routine_id;
    rf_U32 type_id;
    rf_U32 line_no;
    rf_U32 column_no;
} rf_RoutineRecord;

#define RF_ROUTINE_TRACE_MAX_RECORDS 10

typedef struct
{
    rf_RoutineRecord records[RF_ROUTINE_TRACE_MAX_RECORDS];
    rf_U32 depth;
} rf_RoutineTrace;

typedef struct
{
    rf_address ptr;
} rf_MessageHandle;

typedef struct
{
    rf_MessageHandle message_handle;
    rf_RoutineTrace routine_trace;
    rf_U32 file_id;
    rf_U32 routine_id;
    rf_U32 line_no;
    rf_U32 column_no;
} rf_Error;

// ============================================================================
// Shadow stack (debug mode only)
// ============================================================================

#define RF_RUNTIME_STACK_MAX 256

#ifdef _WIN32
#  define RF_THREAD_LOCAL __declspec(thread)
#else
#  define RF_THREAD_LOCAL __thread
#endif

static RF_THREAD_LOCAL rf_RoutineRecord rf_runtime_stack[RF_RUNTIME_STACK_MAX];
static RF_THREAD_LOCAL rf_U32 rf_runtime_stack_depth = 0;

// ============================================================================
// Symbol tables (set by builder-generated code at startup)
// ============================================================================

static const char** rf_file_table    = NULL;
static rf_U32       rf_file_count    = 0;
static const char** rf_routine_table = NULL;
static rf_U32       rf_routine_count = 0;
static const char** rf_type_table    = NULL;
static rf_U32       rf_type_count    = 0;

void __rf_init_symbol_tables(
    const char** file_table,    rf_U32 file_count,
    const char** routine_table, rf_U32 routine_count,
    const char** type_table,    rf_U32 type_count)
{
    rf_file_table    = file_table;    rf_file_count    = file_count;
    rf_routine_table = routine_table; rf_routine_count = routine_count;
    rf_type_table    = type_table;    rf_type_count    = type_count;
}

// ============================================================================
// Platform stack trace (release mode)
// ============================================================================

#ifdef RF_PLATFORM_WINDOWS

static void print_platform_stack(void)
{
    void*  stack[64];
    USHORT frames = RtlCaptureStackBackTrace(
        1,           // skip this helper frame
        64, stack, NULL);

    HANDLE process = GetCurrentProcess();
    SymInitialize(process, NULL, TRUE);

    char         sym_buf[sizeof(SYMBOL_INFO) + MAX_SYM_NAME];
    SYMBOL_INFO* sym = (SYMBOL_INFO*)sym_buf;
    sym->SizeOfStruct = sizeof(SYMBOL_INFO);
    sym->MaxNameLen   = MAX_SYM_NAME;

    fprintf(stderr, "Stack trace:\n");
    for (USHORT i = 0; i < frames; i++)
    {
        DWORD64 addr = (DWORD64)(uintptr_t)stack[i];
        if (SymFromAddr(process, addr, 0, sym))
        {
            fprintf(stderr, "  %u: at %s\n", (unsigned)i, sym->Name);
        }
        else
        {
            fprintf(stderr, "  %u: at 0x%016llx\n", (unsigned)i, (unsigned long long)addr);
        }
    }
}

#else  /* POSIX */

static void print_platform_stack(void)
{
    void*  stack[64];
    int    frames  = backtrace(stack, 64);
    char** symbols = backtrace_symbols(stack, frames);

    fprintf(stderr, "Stack trace:\n");
    if (symbols)
    {
        // Skip frame 0 (this helper) and frame 1 (__rf_throw / signal handler)
        for (int i = 2; i < frames; i++)
        {
            fprintf(stderr, "  %d: %s\n", i - 2, symbols[i]);
        }
        free(symbols);
    }
    else
    {
        for (int i = 2; i < frames; i++)
        {
            fprintf(stderr, "  %d: %p\n", i - 2, stack[i]);
        }
    }
}

#endif

// ============================================================================
// Signal / exception handlers (release mode)
// ============================================================================

#ifdef RF_PLATFORM_WINDOWS

static LONG WINAPI rf_exception_handler(EXCEPTION_POINTERS* ep)
{
    if (rf_trace_mode == RF_TRACE_NONE) return EXCEPTION_CONTINUE_SEARCH;

    const char* name = "Exception";
    switch (ep->ExceptionRecord->ExceptionCode)
    {
        case EXCEPTION_ACCESS_VIOLATION:    name = "AccessViolation";    break;
        case EXCEPTION_INT_DIVIDE_BY_ZERO:  name = "DivisionByZero";     break;
        case EXCEPTION_ILLEGAL_INSTRUCTION: name = "IllegalInstruction"; break;
        case EXCEPTION_STACK_OVERFLOW:      name = "StackOverflow";      break;
    }
    fprintf(stderr, "\033[91m\n%s (code 0x%08lX)\n\033[0m",
            name, ep->ExceptionRecord->ExceptionCode);
    fflush(stderr);
    print_platform_stack();
    fflush(stderr);
    return EXCEPTION_CONTINUE_SEARCH;
}

static void install_signal_handlers(void)
{
    AddVectoredExceptionHandler(1, rf_exception_handler);
}

#else  /* POSIX */

static void rf_signal_handler(int sig)
{
    if (rf_trace_mode == RF_TRACE_NONE) { signal(sig, SIG_DFL); raise(sig); return; }
    const char* name = "Signal";
    switch (sig)
    {
        case SIGSEGV: name = "SegmentationFault"; break;
        case SIGFPE:  name = "FloatingPointException / DivisionByZero"; break;
        case SIGILL:  name = "IllegalInstruction"; break;
        case SIGBUS:  name = "BusError"; break;
        case SIGABRT: name = "Abort"; break;
    }
    fprintf(stderr, "\033[91m\n%s\n\033[0m", name);
    fflush(stderr);
    print_platform_stack();
    fflush(stderr);

    // Restore default handler and re-raise so the process exits with the right code
    signal(sig, SIG_DFL);
    raise(sig);
}

static void install_signal_handlers(void)
{
    signal(SIGSEGV, rf_signal_handler);
    signal(SIGFPE,  rf_signal_handler);
    signal(SIGILL,  rf_signal_handler);
#ifdef SIGBUS
    signal(SIGBUS,  rf_signal_handler);
#endif
    signal(SIGABRT, rf_signal_handler);
}

#endif

// ============================================================================
// Public API — mode selection
// ============================================================================

void __rf_set_trace_mode(int mode)
{
    rf_trace_mode = mode;
    if (mode != RF_TRACE_NONE)
    {
        install_signal_handlers();
    }
}

// ============================================================================
// Shadow stack push/pop (debug mode)
// ============================================================================

void __rf_stack_push(rf_U32 file_id, rf_U32 routine_id, rf_U32 type_id,
                     rf_U32 line_no, rf_U32 column_no)
{
    if (rf_runtime_stack_depth >= RF_RUNTIME_STACK_MAX)
    {
        fprintf(stderr, "\033[31m\nRazorForge Runtime Error: Stack overflow (depth > %d)\n\033[0m",
                RF_RUNTIME_STACK_MAX);
        fflush(stderr);
        exit(1);
    }
    rf_RoutineRecord* r = &rf_runtime_stack[rf_runtime_stack_depth++];
    r->file_id    = file_id;
    r->routine_id = routine_id;
    r->type_id    = type_id;
    r->line_no    = line_no;
    r->column_no  = column_no;
}

void __rf_stack_pop(void)
{
    if (rf_runtime_stack_depth > 0) rf_runtime_stack_depth--;
}

// ============================================================================
// Shadow stack capture / print (debug mode)
// ============================================================================

void __rf_stack_capture(rf_RoutineTrace* out)
{
    if (!out) return;
    rf_U32 n = rf_runtime_stack_depth < RF_ROUTINE_TRACE_MAX_RECORDS
               ? rf_runtime_stack_depth : RF_ROUTINE_TRACE_MAX_RECORDS;
    for (rf_U32 i = 0; i < n; i++)
        out->records[i] = rf_runtime_stack[rf_runtime_stack_depth - 1 - i];
    for (rf_U32 i = n; i < RF_ROUTINE_TRACE_MAX_RECORDS; i++)
        memset(&out->records[i], 0, sizeof(rf_RoutineRecord));
    out->depth = n;
}

static const char* get_file_name(rf_U32 id)
{
    return (rf_file_table && id < rf_file_count) ? rf_file_table[id] : "<unknown>";
}
static const char* get_routine_name(rf_U32 id)
{
    return (rf_routine_table && id < rf_routine_count) ? rf_routine_table[id] : "<unknown>";
}
static const char* get_type_name(rf_U32 id)
{
    if (id == 0) return NULL;
    return (rf_type_table && id < rf_type_count) ? rf_type_table[id] : "<unknown>";
}

static void print_shadow_stack(void)
{
    if (rf_runtime_stack_depth == 0)
    {
        fprintf(stderr, "  <stack empty>\n");
        return;
    }
    fprintf(stderr, "Stack trace:\n");
    for (rf_U32 i = 0; i < rf_runtime_stack_depth; i++)
    {
        rf_U32 idx = rf_runtime_stack_depth - 1 - i;
        const rf_RoutineRecord* r = &rf_runtime_stack[idx];
        const char* file    = get_file_name(r->file_id);
        const char* routine = get_routine_name(r->routine_id);
        const char* type    = get_type_name(r->type_id);
        if (type)
            fprintf(stderr, "  %u: at %s.%s (%s:%u:%u)\n",
                    (unsigned)i, type, routine, file, r->line_no, r->column_no);
        else
            fprintf(stderr, "  %u: at %s (%s:%u:%u)\n",
                    (unsigned)i, routine, file, r->line_no, r->column_no);
    }
}

void __rf_print_routine_trace(const rf_RoutineTrace* trace)
{
    if (!trace || trace->depth == 0) { fprintf(stderr, "  <no trace>\n"); return; }
    fprintf(stderr, "Stack trace:\n");
    for (rf_U32 i = 0; i < trace->depth; i++)
    {
        const rf_RoutineRecord* r = &trace->records[i];
        const char* type = get_type_name(r->type_id);
        if (type)
            fprintf(stderr, "  %u: at %s.%s (%s:%u:%u)\n", (unsigned)i,
                    type, get_routine_name(r->routine_id),
                    get_file_name(r->file_id), r->line_no, r->column_no);
        else
            fprintf(stderr, "  %u: at %s (%s:%u:%u)\n", (unsigned)i,
                    get_routine_name(r->routine_id),
                    get_file_name(r->file_id), r->line_no, r->column_no);
    }
}

void __rf_print_current_stack(void) { print_shadow_stack(); }

// ============================================================================
// Core throw — dispatches based on trace mode
// ============================================================================

static void print_stack_for_mode(void)
{
    switch (rf_trace_mode)
    {
        case RF_TRACE_SHADOW:   print_shadow_stack();   break;
        case RF_TRACE_PLATFORM: print_platform_stack(); break;
        default: break; /* RF_TRACE_NONE */
    }
}

void __rf_print_stack_for_mode(void)  { print_stack_for_mode(); }
int  __rf_get_trace_mode(void)        { return rf_trace_mode; }

void __rf_throw(const char* error_type, const char* message)
{
    fprintf(stderr, "\033[91m\n%s: %s\n",
            error_type ? error_type : "Error",
            message    ? message    : "");
    print_stack_for_mode();
    fprintf(stderr, "\033[0m\n");
    fflush(stderr);
    exit(1);
}

void __rf_throw_absent(void)
{
    __rf_throw("AbsentValueError", "Attempted to access an absent value");
}

void __rf_throw_division_by_zero(void)
{
    __rf_throw("DivisionByZeroError", "Division by zero");
}

void __rf_throw_index_out_of_bounds(rf_U32 index, rf_U32 count)
{
    char buf[128];
    snprintf(buf, sizeof(buf),
             "Index %u is out of bounds for collection with %u elements", index, count);
    __rf_throw("IndexOutOfBoundsError", buf);
}

void __rf_throw_integer_overflow(const char* message)
{
    __rf_throw("IntegerOverflowError",
               message ? message : "An integer overflow occurred");
}

void __rf_throw_empty_collection(const char* operation)
{
    char buf[128];
    snprintf(buf, sizeof(buf), "Cannot %s on empty collection",
             operation ? operation : "perform operation");
    __rf_throw("EmptyCollectionError", buf);
}

void __rf_throw_element_not_found(void)
{
    __rf_throw("ElementNotFoundError", "No matching element found");
}

// ============================================================================
// Error record (used by failable routines)
// ============================================================================

void __rf_create_error(
    rf_Error* out, const char* message,
    rf_U32 file_id, rf_U32 routine_id, rf_U32 line_no, rf_U32 column_no)
{
    if (!out) return;
    out->message_handle.ptr = (rf_address)message;
    __rf_stack_capture(&out->routine_trace);
    out->file_id    = file_id;
    out->routine_id = routine_id;
    out->line_no    = line_no;
    out->column_no  = column_no;
}

void __rf_print_error(const rf_Error* error)
{
    if (!error) return;
    fprintf(stderr, "\033[91m\nError at %s:%u:%u in %s\n",
            get_file_name(error->file_id), error->line_no, error->column_no,
            get_routine_name(error->routine_id));
    const char* msg = (const char*)error->message_handle.ptr;
    if (msg) fprintf(stderr, "  %s\n\n", msg);
    __rf_print_routine_trace(&error->routine_trace);
    fprintf(stderr, "\033[0m");
}