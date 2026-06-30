// runtime_init.c - Runtime process initialization

#ifdef _WIN32
#include <windows.h>
#endif

/* Start the async I/O event loop on the main thread (async_io.c). Done here so libuv init never runs
 * on a demand-paged coroutine green stack, which the Windows deep-frame CRT/Win32 paths reject. */
extern void rf_io_runtime_init(void);

void rf_runtime_init(void)
{
    rf_io_runtime_init();
#ifdef _WIN32
    SetConsoleCP(65001);
    SetConsoleOutputCP(65001);

    // Enable ANSI escape sequences for colored error output.
    HANDLE hErr = GetStdHandle(STD_ERROR_HANDLE);
    if (hErr != INVALID_HANDLE_VALUE)
    {
        DWORD mode = 0;
        if (GetConsoleMode(hErr, &mode))
        {
            SetConsoleMode(hErr, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
    }
#endif
}
