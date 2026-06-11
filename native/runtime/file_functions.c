/**
 * @file file_functions.c
 * @brief File I/O and filesystem operations for RazorForge runtime
 *
 * Provides cross-platform file handling (open/read/write/close) and
 * filesystem operations (exists, mkdir, copy, move, remove, path utils).
 *
 * Platform handling:
 *   - Windows: Win32 API (CreateFileA, ReadFile, WriteFile, etc.)
 *   - POSIX: standard open/read/write/stat/mkdir
 *
 * All path functions use '/' as canonical separator internally.
 * Heap-allocated return buffers must be freed by the caller (rf_invalidate).
 */

#include "types.h"
#include <string.h>
#include <stdlib.h>
#include <stdio.h>

#ifdef _WIN32
#include <windows.h>
#include <io.h>
#include <direct.h>
#include <sys/stat.h>
#else
#include <unistd.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <dirent.h>
#include <fcntl.h>
#include <errno.h>
#include <limits.h>
#endif

/* Forward declaration from memory.c */
extern void* rf_allocate_dynamic(uint64_t bytes);

/* ========================================================================== */
/* Global result length — avoids out-parameters that RF can't easily pass     */
/* ========================================================================== */
static rf_address rf_last_result_len = 0;

rf_address rf_get_result_len(void)
{
    return rf_last_result_len;
}

/* ========================================================================== */
/* Helper: null-terminate a counted string                                     */
/* ========================================================================== */

static char* make_cstr(const char* path, rf_S32 len)
{
    char* buf = (char*)malloc((size_t)len + 1);
    if (!buf) return NULL;
    memcpy(buf, path, (size_t)len);
    buf[len] = '\0';
    return buf;
}

/* ========================================================================== */
/* File I/O                                                                    */
/* ========================================================================== */

/* ── Handle table: FILE* stored by index, returned as S32 ── */
#define RF_MAX_OPEN_FILES 256
static FILE* rf_file_table[RF_MAX_OPEN_FILES];
static int rf_file_table_init = 0;

static void rf_file_table_ensure_init(void)
{
    if (!rf_file_table_init)
    {
        for (int i = 0; i < RF_MAX_OPEN_FILES; i++)
            rf_file_table[i] = NULL;
        rf_file_table_init = 1;
    }
}

static rf_S32 rf_file_table_add(FILE* f)
{
    rf_file_table_ensure_init();
    for (int i = 0; i < RF_MAX_OPEN_FILES; i++)
    {
        if (rf_file_table[i] == NULL)
        {
            rf_file_table[i] = f;
            return (rf_S32)i;
        }
    }
    return -1; /* table full */
}

static FILE* fd_to_file(rf_S32 fd)
{
    rf_file_table_ensure_init();
    if (fd < 0 || fd >= RF_MAX_OPEN_FILES) return NULL;
    return rf_file_table[fd];
}

/**
 * Open file by path.
 * mode: 0=READ, 1=WRITE, 2=APPEND
 * Returns handle (>= 0) on success, -1 on error.
 */
rf_S32 rf_file_open(const char* path, rf_S32 path_len, rf_S32 mode)
{
    char* cpath = make_cstr(path, path_len);
    if (!cpath) return -1;

    FILE* f = NULL;
    switch (mode)
    {
        case 0: f = fopen(cpath, "rb"); break;
        case 1: f = fopen(cpath, "wb"); break;
        case 2: f = fopen(cpath, "ab"); break;
        default: free(cpath); return -1;
    }

    free(cpath);
    if (!f) return -1;

    rf_S32 handle = rf_file_table_add(f);
    if (handle < 0) { fclose(f); return -1; }
    return handle;
}

void rf_file_close(rf_S32 fd)
{
    FILE* f = fd_to_file(fd);
    if (f)
    {
        fclose(f);
        rf_file_table[fd] = NULL;
    }
}

/**
 * Read entire file into heap-allocated buffer.
 * Sets rf_last_result_len to the number of bytes read.
 * Returns pointer to buffer (caller frees), or NULL on error.
 */
char* rf_file_read_all(rf_S32 fd)
{
    FILE* f = fd_to_file(fd);
    if (!f) { rf_last_result_len = 0; return NULL; }

    /* Determine file size */
    long start = ftell(f);
    fseek(f, 0, SEEK_END);
    long end = ftell(f);
    fseek(f, start, SEEK_SET);

    size_t remaining = (size_t)(end - start);
    char* buf = (char*)malloc(remaining + 1);
    if (!buf) { rf_last_result_len = 0; return NULL; }

    size_t total_read = fread(buf, 1, remaining, f);
    buf[total_read] = '\0';
    rf_last_result_len = (rf_address)total_read;
    return buf;
}

/**
 * Read one line (up to \n or EOF).
 * Returns heap-allocated string, sets rf_last_result_len.
 * The trailing \n is NOT included.
 */
char* rf_file_read_line(rf_S32 fd)
{
    FILE* f = fd_to_file(fd);
    if (!f) { rf_last_result_len = 0; return NULL; }

    size_t capacity = 256;
    size_t length = 0;
    char* buf = (char*)malloc(capacity);
    if (!buf) { rf_last_result_len = 0; return NULL; }

    int c;
    while ((c = fgetc(f)) != EOF && c != '\n')
    {
        if (c == '\r') continue; /* skip CR */
        if (length + 1 >= capacity)
        {
            capacity *= 2;
            char* newbuf = (char*)realloc(buf, capacity);
            if (!newbuf) { free(buf); rf_last_result_len = 0; return NULL; }
            buf = newbuf;
        }
        buf[length++] = (char)c;
    }
    buf[length] = '\0';
    rf_last_result_len = (rf_address)length;
    return buf;
}

/**
 * Read up to `count` bytes.
 * Returns heap-allocated buffer, sets rf_last_result_len to actual bytes read.
 */
char* rf_file_read_bytes(rf_S32 fd, rf_address count)
{
    FILE* f = fd_to_file(fd);
    if (!f) { rf_last_result_len = 0; return NULL; }

    char* buf = (char*)malloc((size_t)count + 1);
    if (!buf) { rf_last_result_len = 0; return NULL; }

    size_t actual = fread(buf, 1, (size_t)count, f);
    buf[actual] = '\0';
    rf_last_result_len = (rf_address)actual;
    return buf;
}

/**
 * Write `len` bytes from `ptr` to file.
 * Returns number of bytes written.
 */
rf_address rf_file_write(rf_S32 fd, const char* ptr, rf_address len)
{
    FILE* f = fd_to_file(fd);
    if (!f) return 0;
    size_t written = fwrite(ptr, 1, (size_t)len, f);
    return (rf_address)written;
}

void rf_file_flush(rf_S32 fd)
{
    FILE* f = fd_to_file(fd);
    if (f) fflush(f);
}

rf_Bool rf_file_is_eof(rf_S32 fd)
{
    FILE* f = fd_to_file(fd);
    if (!f) return true;
    return feof(f) != 0;
}

/* ========================================================================== */
/* FileSystem: Existence checks                                                */
/* ========================================================================== */

rf_Bool rf_fs_exists(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return false;

#ifdef _WIN32
    DWORD attr = GetFileAttributesA(cpath);
    free(cpath);
    return attr != INVALID_FILE_ATTRIBUTES;
#else
    struct stat st;
    int ret = stat(cpath, &st);
    free(cpath);
    return ret == 0;
#endif
}

rf_Bool rf_fs_is_file(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return false;

#ifdef _WIN32
    DWORD attr = GetFileAttributesA(cpath);
    free(cpath);
    return (attr != INVALID_FILE_ATTRIBUTES) && !(attr & FILE_ATTRIBUTE_DIRECTORY);
#else
    struct stat st;
    int ret = stat(cpath, &st);
    free(cpath);
    return ret == 0 && S_ISREG(st.st_mode);
#endif
}

rf_Bool rf_fs_is_directory(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return false;

#ifdef _WIN32
    DWORD attr = GetFileAttributesA(cpath);
    free(cpath);
    return (attr != INVALID_FILE_ATTRIBUTES) && (attr & FILE_ATTRIBUTE_DIRECTORY);
#else
    struct stat st;
    int ret = stat(cpath, &st);
    free(cpath);
    return ret == 0 && S_ISDIR(st.st_mode);
#endif
}

/* ========================================================================== */
/* FileSystem: Directory operations                                            */
/* ========================================================================== */

rf_S32 rf_fs_create_dir(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return -1;

#ifdef _WIN32
    BOOL ok = CreateDirectoryA(cpath, NULL);
    free(cpath);
    return ok ? 0 : -1;
#else
    int ret = mkdir(cpath, 0755);
    free(cpath);
    return ret == 0 ? 0 : -1;
#endif
}

/**
 * Create directories recursively (like mkdir -p).
 */
rf_S32 rf_fs_create_dir_all(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return -1;

    /* Walk through path components and create each */
    char* p = cpath;
    /* Skip leading slash or drive letter */
#ifdef _WIN32
    if (p[0] && p[1] == ':') p += 2;
#endif
    if (*p == '/' || *p == '\\') p++;

    for (; *p; p++)
    {
        if (*p == '/' || *p == '\\')
        {
            char saved = *p;
            *p = '\0';
#ifdef _WIN32
            CreateDirectoryA(cpath, NULL);
#else
            mkdir(cpath, 0755);
#endif
            *p = saved;
        }
    }
    /* Create the final directory */
#ifdef _WIN32
    BOOL ok = CreateDirectoryA(cpath, NULL);
    DWORD err = GetLastError();
    free(cpath);
    return (ok || err == ERROR_ALREADY_EXISTS) ? 0 : -1;
#else
    int ret = mkdir(cpath, 0755);
    free(cpath);
    return (ret == 0 || errno == EEXIST) ? 0 : -1;
#endif
}

rf_S32 rf_fs_remove(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return -1;

#ifdef _WIN32
    DWORD attr = GetFileAttributesA(cpath);
    if (attr == INVALID_FILE_ATTRIBUTES) { free(cpath); return -1; }
    BOOL ok;
    if (attr & FILE_ATTRIBUTE_DIRECTORY)
        ok = RemoveDirectoryA(cpath);
    else
        ok = DeleteFileA(cpath);
    free(cpath);
    return ok ? 0 : -1;
#else
    struct stat st;
    if (stat(cpath, &st) != 0) { free(cpath); return -1; }
    int ret;
    if (S_ISDIR(st.st_mode))
        ret = rmdir(cpath);
    else
        ret = unlink(cpath);
    free(cpath);
    return ret == 0 ? 0 : -1;
#endif
}

/**
 * Recursively remove a directory and all contents.
 */
rf_S32 rf_fs_remove_all(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return -1;

#ifdef _WIN32
    /* Check if it's a file first */
    DWORD attr = GetFileAttributesA(cpath);
    if (attr == INVALID_FILE_ATTRIBUTES) { free(cpath); return -1; }
    if (!(attr & FILE_ATTRIBUTE_DIRECTORY))
    {
        BOOL ok = DeleteFileA(cpath);
        free(cpath);
        return ok ? 0 : -1;
    }

    /* Build search pattern: path\* */
    size_t plen = strlen(cpath);
    char* pattern = (char*)malloc(plen + 3);
    if (!pattern) { free(cpath); return -1; }
    memcpy(pattern, cpath, plen);
    pattern[plen] = '\\';
    pattern[plen + 1] = '*';
    pattern[plen + 2] = '\0';

    WIN32_FIND_DATAA fd;
    HANDLE hFind = FindFirstFileA(pattern, &fd);
    free(pattern);

    if (hFind != INVALID_HANDLE_VALUE)
    {
        do
        {
            if (strcmp(fd.cFileName, ".") == 0 || strcmp(fd.cFileName, "..") == 0)
                continue;

            size_t child_len = plen + 1 + strlen(fd.cFileName);
            char* child = (char*)malloc(child_len + 1);
            if (!child) continue;
            snprintf(child, child_len + 1, "%s\\%s", cpath, fd.cFileName);

            rf_fs_remove_all(child, (rf_S32)strlen(child));
            free(child);
        } while (FindNextFileA(hFind, &fd));
        FindClose(hFind);
    }

    BOOL ok = RemoveDirectoryA(cpath);
    free(cpath);
    return ok ? 0 : -1;
#else
    struct stat st;
    if (lstat(cpath, &st) != 0) { free(cpath); return -1; }

    if (!S_ISDIR(st.st_mode))
    {
        int ret = unlink(cpath);
        free(cpath);
        return ret == 0 ? 0 : -1;
    }

    DIR* dir = opendir(cpath);
    if (!dir) { free(cpath); return -1; }

    struct dirent* entry;
    while ((entry = readdir(dir)) != NULL)
    {
        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0)
            continue;

        size_t plen = strlen(cpath);
        size_t child_len = plen + 1 + strlen(entry->d_name);
        char* child = (char*)malloc(child_len + 1);
        if (!child) continue;
        snprintf(child, child_len + 1, "%s/%s", cpath, entry->d_name);

        rf_fs_remove_all(child, (rf_S32)strlen(child));
        free(child);
    }
    closedir(dir);

    int ret = rmdir(cpath);
    free(cpath);
    return ret == 0 ? 0 : -1;
#endif
}

/* ========================================================================== */
/* FileSystem: File operations (copy, move)                                    */
/* ========================================================================== */

rf_S32 rf_fs_copy(const char* src, rf_S32 src_len, const char* dst, rf_S32 dst_len)
{
    char* csrc = make_cstr(src, src_len);
    char* cdst = make_cstr(dst, dst_len);
    if (!csrc || !cdst) { free(csrc); free(cdst); return -1; }

#ifdef _WIN32
    BOOL ok = CopyFileA(csrc, cdst, FALSE);
    free(csrc); free(cdst);
    return ok ? 0 : -1;
#else
    FILE* fin = fopen(csrc, "rb");
    if (!fin) { free(csrc); free(cdst); return -1; }

    FILE* fout = fopen(cdst, "wb");
    if (!fout) { fclose(fin); free(csrc); free(cdst); return -1; }

    char buf[8192];
    size_t n;
    while ((n = fread(buf, 1, sizeof(buf), fin)) > 0)
        fwrite(buf, 1, n, fout);

    fclose(fin);
    fclose(fout);
    free(csrc); free(cdst);
    return 0;
#endif
}

rf_S32 rf_fs_move(const char* src, rf_S32 src_len, const char* dst, rf_S32 dst_len)
{
    char* csrc = make_cstr(src, src_len);
    char* cdst = make_cstr(dst, dst_len);
    if (!csrc || !cdst) { free(csrc); free(cdst); return -1; }

#ifdef _WIN32
    BOOL ok = MoveFileExA(csrc, cdst, MOVEFILE_REPLACE_EXISTING);
    free(csrc); free(cdst);
    return ok ? 0 : -1;
#else
    int ret = rename(csrc, cdst);
    if (ret != 0)
    {
        /* rename may fail across filesystems; fallback to copy + remove */
        ret = rf_fs_copy(csrc, (rf_S32)strlen(csrc), cdst, (rf_S32)strlen(cdst));
        if (ret == 0) unlink(csrc);
    }
    free(csrc); free(cdst);
    return ret == 0 ? 0 : -1;
#endif
}

/* ========================================================================== */
/* FileSystem: Directory listing                                               */
/* ========================================================================== */

/**
 * List directory entries.
 * Returns null-separated list of names. Sets *out_count to number of entries,
 * *out_total_len to total byte length of the returned buffer.
 */
/* list_dir deferred — needs returning List[Text] from C */

/* ========================================================================== */
/* FileSystem: Metadata                                                        */
/* ========================================================================== */

/* metadata deferred — needs FileMetadata record type in RF stdlib */

/**
 * Get file size in bytes. Returns -1 on error.
 */
rf_S64 rf_fs_file_size(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return -1;

#ifdef _WIN32
    WIN32_FILE_ATTRIBUTE_DATA data;
    if (!GetFileAttributesExA(cpath, GetFileExInfoStandard, &data))
    {
        free(cpath);
        return -1;
    }
    free(cpath);
    return (rf_S64)(((rf_U64)data.nFileSizeHigh << 32) | (rf_U64)data.nFileSizeLow);
#else
    struct stat st;
    if (stat(cpath, &st) != 0) { free(cpath); return -1; }
    free(cpath);
    return (rf_S64)st.st_size;
#endif
}

/* ========================================================================== */
/* FileSystem: Path utilities (pure string operations, no OS calls)            */
/* ========================================================================== */

static int is_sep(char c)
{
    return c == '/' || c == '\\';
}

char* rf_fs_join_path(const char* a, rf_S32 a_len, const char* b, rf_S32 b_len)
{
    /* a + "/" + b */
    int need_sep = (a_len > 0 && !is_sep(a[a_len - 1])) ? 1 : 0;
    size_t total = (size_t)a_len + need_sep + (size_t)b_len;
    char* buf = (char*)malloc(total + 1);
    if (!buf) { rf_last_result_len = 0; return NULL; }

    memcpy(buf, a, (size_t)a_len);
    if (need_sep) buf[a_len] = '/';
    memcpy(buf + a_len + need_sep, b, (size_t)b_len);
    buf[total] = '\0';
    rf_last_result_len = (rf_address)total;
    return buf;
}

/**
 * Return the parent directory of a path.
 * "/a/b/c" -> "/a/b", "a/b" -> "a", "file.txt" -> ""
 */
char* rf_fs_parent_path(const char* path, rf_S32 len)
{
    if (len <= 0) { rf_last_result_len = 0; return (char*)malloc(1); }

    /* Skip trailing separators */
    int end = len - 1;
    while (end > 0 && is_sep(path[end])) end--;

    /* Find last separator */
    int last_sep = -1;
    for (int i = end; i >= 0; i--)
    {
        if (is_sep(path[i])) { last_sep = i; break; }
    }

    if (last_sep < 0)
    {
        /* No separator found - no parent */
        char* buf = (char*)malloc(1);
        if (buf) buf[0] = '\0';
        rf_last_result_len = 0;
        return buf;
    }

    /* Skip trailing seps in parent */
    int pend = last_sep;
    while (pend > 0 && is_sep(path[pend - 1])) pend--;
    if (pend == 0 && is_sep(path[0])) pend = 1; /* keep root "/" */

    char* buf = (char*)malloc((size_t)pend + 1);
    if (!buf) { rf_last_result_len = 0; return NULL; }
    memcpy(buf, path, (size_t)pend);
    buf[pend] = '\0';
    rf_last_result_len = (rf_address)pend;
    return buf;
}

/**
 * Return the file name component.
 * "/a/b/file.txt" -> "file.txt", "file.txt" -> "file.txt"
 */
char* rf_fs_file_name(const char* path, rf_S32 len)
{
    if (len <= 0) { rf_last_result_len = 0; char* b = (char*)malloc(1); if (b) b[0] = '\0'; return b; }

    /* Skip trailing separators */
    int end = len;
    while (end > 0 && is_sep(path[end - 1])) end--;

    /* Find last separator */
    int start = end;
    for (int i = end - 1; i >= 0; i--)
    {
        if (is_sep(path[i])) { start = i + 1; break; }
        if (i == 0) start = 0;
    }

    size_t name_len = (size_t)(end - start);
    char* buf = (char*)malloc(name_len + 1);
    if (!buf) { rf_last_result_len = 0; return NULL; }
    memcpy(buf, path + start, name_len);
    buf[name_len] = '\0';
    rf_last_result_len = (rf_address)name_len;
    return buf;
}

/**
 * Return the file stem (name without extension).
 * "file.txt" -> "file", "archive.tar.gz" -> "archive.tar"
 */
char* rf_fs_file_stem(const char* path, rf_S32 len)
{
    char* name = rf_fs_file_name(path, len);
    rf_address name_len = rf_last_result_len;
    if (!name || name_len == 0) { rf_last_result_len = 0; return name; }

    /* Find last '.' in name */
    int dot_pos = -1;
    for (int i = (int)name_len - 1; i > 0; i--)
    {
        if (name[i] == '.') { dot_pos = i; break; }
    }

    if (dot_pos <= 0)
    {
        /* No extension or hidden file (starts with .) */
        rf_last_result_len = name_len;
        return name;
    }

    name[dot_pos] = '\0';
    rf_last_result_len = (rf_address)dot_pos;
    return name;
}

/**
 * Return the file extension (including dot).
 * "file.txt" -> ".txt", "file" -> ""
 */
char* rf_fs_extension(const char* path, rf_S32 len)
{
    char* name = rf_fs_file_name(path, len);
    rf_address name_len = rf_last_result_len;
    if (!name) { rf_last_result_len = 0; return NULL; }

    /* Find last '.' in name */
    int dot_pos = -1;
    for (int i = (int)name_len - 1; i > 0; i--)
    {
        if (name[i] == '.') { dot_pos = i; break; }
    }

    if (dot_pos <= 0)
    {
        free(name);
        char* buf = (char*)malloc(1);
        if (buf) buf[0] = '\0';
        rf_last_result_len = 0;
        return buf;
    }

    size_t ext_len = (size_t)(name_len - (rf_address)dot_pos);
    char* buf = (char*)malloc(ext_len + 1);
    if (!buf) { free(name); rf_last_result_len = 0; return NULL; }
    memcpy(buf, name + dot_pos, ext_len);
    buf[ext_len] = '\0';
    rf_last_result_len = (rf_address)ext_len;
    free(name);
    return buf;
}

/* ========================================================================== */
/* FileSystem: Path resolution (OS calls)                                      */
/* ========================================================================== */

char* rf_fs_absolute_path(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) { rf_last_result_len = 0; return NULL; }

#ifdef _WIN32
    char resolved[MAX_PATH];
    DWORD ret = GetFullPathNameA(cpath, MAX_PATH, resolved, NULL);
    free(cpath);
    if (ret == 0 || ret >= MAX_PATH) { rf_last_result_len = 0; return NULL; }
    size_t rlen = strlen(resolved);
    char* buf = (char*)malloc(rlen + 1);
    if (!buf) { rf_last_result_len = 0; return NULL; }
    memcpy(buf, resolved, rlen + 1);
    rf_last_result_len = (rf_address)rlen;
    return buf;
#else
    char* resolved = realpath(cpath, NULL);
    free(cpath);
    if (!resolved)
    {
        /* If file doesn't exist, try to compose absolute path manually */
        cpath = make_cstr(path, len);
        if (!cpath) { rf_last_result_len = 0; return NULL; }

        char cwd[PATH_MAX];
        if (!getcwd(cwd, sizeof(cwd))) { free(cpath); rf_last_result_len = 0; return NULL; }

        size_t cwd_len = strlen(cwd);
        size_t total = cwd_len + 1 + (size_t)len;
        char* buf = (char*)malloc(total + 1);
        if (!buf) { free(cpath); rf_last_result_len = 0; return NULL; }
        memcpy(buf, cwd, cwd_len);
        buf[cwd_len] = '/';
        memcpy(buf + cwd_len + 1, cpath, (size_t)len);
        buf[total] = '\0';
        free(cpath);
        rf_last_result_len = (rf_address)total;
        return buf;
    }
    size_t rlen = strlen(resolved);
    rf_last_result_len = (rf_address)rlen;
    return resolved; /* already heap-allocated by realpath */
#endif
}

char* rf_fs_canonical_path(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) { rf_last_result_len = 0; return NULL; }

#ifdef _WIN32
    char resolved[MAX_PATH];
    DWORD ret = GetFullPathNameA(cpath, MAX_PATH, resolved, NULL);
    free(cpath);
    if (ret == 0 || ret >= MAX_PATH) { rf_last_result_len = 0; return NULL; }

    /* Verify the path exists for canonical */
    DWORD attr = GetFileAttributesA(resolved);
    if (attr == INVALID_FILE_ATTRIBUTES) { rf_last_result_len = 0; return NULL; }

    size_t rlen = strlen(resolved);
    char* buf = (char*)malloc(rlen + 1);
    if (!buf) { rf_last_result_len = 0; return NULL; }
    memcpy(buf, resolved, rlen + 1);
    rf_last_result_len = (rf_address)rlen;
    return buf;
#else
    char* resolved = realpath(cpath, NULL);
    free(cpath);
    if (!resolved) { rf_last_result_len = 0; return NULL; }
    size_t rlen = strlen(resolved);
    rf_last_result_len = (rf_address)rlen;
    return resolved;
#endif
}

/* ========================================================================== */
/* FileSystem: Permissions                                                     */
/* ========================================================================== */

rf_Bool rf_fs_can_read(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return false;

#ifdef _WIN32
    /* On Windows, if the file exists and isn't exclusively locked, it's readable */
    DWORD attr = GetFileAttributesA(cpath);
    free(cpath);
    return attr != INVALID_FILE_ATTRIBUTES;
#else
    int ret = access(cpath, R_OK);
    free(cpath);
    return ret == 0;
#endif
}

rf_Bool rf_fs_can_write(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return false;

#ifdef _WIN32
    DWORD attr = GetFileAttributesA(cpath);
    free(cpath);
    return (attr != INVALID_FILE_ATTRIBUTES) && !(attr & FILE_ATTRIBUTE_READONLY);
#else
    int ret = access(cpath, W_OK);
    free(cpath);
    return ret == 0;
#endif
}

rf_Bool rf_fs_can_execute(const char* path, rf_S32 len)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return false;

#ifdef _WIN32
    /* On Windows, check common executable extensions */
    DWORD attr = GetFileAttributesA(cpath);
    if (attr == INVALID_FILE_ATTRIBUTES || (attr & FILE_ATTRIBUTE_DIRECTORY))
    {
        free(cpath);
        return false;
    }
    /* Check extension */
    const char* ext = strrchr(cpath, '.');
    rf_Bool result = false;
    if (ext)
    {
        result = (_stricmp(ext, ".exe") == 0 ||
                  _stricmp(ext, ".cmd") == 0 ||
                  _stricmp(ext, ".bat") == 0 ||
                  _stricmp(ext, ".com") == 0);
    }
    free(cpath);
    return result;
#else
    int ret = access(cpath, X_OK);
    free(cpath);
    return ret == 0;
#endif
}

rf_S32 rf_fs_set_readonly(const char* path, rf_S32 len, rf_Bool readonly)
{
    char* cpath = make_cstr(path, len);
    if (!cpath) return -1;

#ifdef _WIN32
    DWORD attr = GetFileAttributesA(cpath);
    if (attr == INVALID_FILE_ATTRIBUTES) { free(cpath); return -1; }

    if (readonly)
        attr |= FILE_ATTRIBUTE_READONLY;
    else
        attr &= ~FILE_ATTRIBUTE_READONLY;

    BOOL ok = SetFileAttributesA(cpath, attr);
    free(cpath);
    return ok ? 0 : -1;
#else
    struct stat st;
    if (stat(cpath, &st) != 0) { free(cpath); return -1; }

    mode_t mode;
    if (readonly)
        mode = st.st_mode & ~(S_IWUSR | S_IWGRP | S_IWOTH);
    else
        mode = st.st_mode | S_IWUSR;

    int ret = chmod(cpath, mode);
    free(cpath);
    return ret == 0 ? 0 : -1;
#endif
}
