/*
 * ytdlp_host —— yt-dlp.exe 进程宿主实现
 *
 * 线程模型：每个管道一个专用阻塞读线程 + 一个监视线程（等待读线程结束后触发 on_exit）。
 * 引用计数：初始 1（托管方）+ 每线程 1；归零时关闭全部句柄并释放结构体。
 * Job Object：KILL_ON_JOB_CLOSE，保证应用崩溃/退出时也不会遗留 yt-dlp / ffmpeg 进程。
 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
#include <wctype.h>

#include "ytdlp_host.h"

#define YTDLP_HOST_VERSION "1.0.0"
#define READ_CHUNK (32 * 1024)
#define PIPE_BUF_SIZE (1024 * 1024)
#define CANCEL_EXIT_CODE 0x00010004u /* STATUS_CONTROL_C_EXIT */

typedef struct {
    HANDLE process_handle;
    HANDLE job_handle;
    HANDLE stdout_read;
    HANDLE stderr_read;
    HANDLE stdout_thread;
    HANDLE stderr_thread;
    CRITICAL_SECTION lock;
    volatile LONG ref_count;
    volatile LONG exit_code;
    volatile LONG exit_fired;
    ytdlp_line_cb on_line;
    ytdlp_exit_cb on_exit;
    void* user;
} ytdlp_process_t;

typedef struct {
    ytdlp_process_t* p;
    HANDLE handle;
    int is_stderr;
} reader_ctx_t;

static void proc_release(ytdlp_process_t* p)
{
    if (InterlockedDecrement(&p->ref_count) != 0)
        return;
    if (p->stdout_read)
        CloseHandle(p->stdout_read);
    if (p->stderr_read)
        CloseHandle(p->stderr_read);
    if (p->process_handle)
        CloseHandle(p->process_handle);
    if (p->stdout_thread)
        CloseHandle(p->stdout_thread);
    if (p->stderr_thread)
        CloseHandle(p->stderr_thread);
    if (p->job_handle)
        CloseHandle(p->job_handle); /* KILL_ON_JOB_CLOSE：最后一个引用消失时兜底清场 */
    DeleteCriticalSection(&p->lock);
    free(p);
}

static wchar_t* utf8_to_wide(const char* s)
{
    int n;
    wchar_t* w;
    if (!s)
        return NULL;
    n = MultiByteToWideChar(CP_UTF8, 0, s, -1, NULL, 0);
    if (n <= 0)
        return NULL;
    w = (wchar_t*)malloc((size_t)n * sizeof(wchar_t));
    if (!w)
        return NULL;
    if (MultiByteToWideChar(CP_UTF8, 0, s, -1, w, n) <= 0) {
        free(w);
        return NULL;
    }
    return w;
}

static int ci_has_prefix(const wchar_t* s, const wchar_t* prefix)
{
    size_t i;
    for (i = 0; prefix[i]; i++) {
        if (towlower(s[i]) != prefix[i])
            return 0;
    }
    return 1;
}

/* 父进程环境 + 强制 Python UTF-8（yt-dlp 是 PyInstaller 打包的 Python 程序） */
static wchar_t* build_env_block(void)
{
    static const wchar_t* extras[2] = { L"PYTHONUTF8=1", L"PYTHONIOENCODING=utf-8" };
    wchar_t* parent;
    wchar_t* block;
    wchar_t* w;
    size_t total = 0;
    int pass;

    for (pass = 0; pass < 2; pass++) {
        size_t running = 0;
        parent = GetEnvironmentStringsW();
        if (parent) {
            wchar_t* q = parent;
            while (*q) {
                size_t l = wcslen(q);
                if (!ci_has_prefix(q, L"pythonutf8=") && !ci_has_prefix(q, L"pythonioencoding="))
                    running += l + 1;
                q += l + 1;
            }
            FreeEnvironmentStringsW(parent);
        }
        if (pass == 0) {
            total = running + wcslen(extras[0]) + 1 + wcslen(extras[1]) + 1;
            block = (wchar_t*)malloc((total + 1) * sizeof(wchar_t));
            if (!block)
                return NULL;
            w = block;
        }
        else {
            parent = GetEnvironmentStringsW();
            if (parent) {
                wchar_t* q = parent;
                while (*q) {
                    size_t l = wcslen(q);
                    if (!ci_has_prefix(q, L"pythonutf8=") && !ci_has_prefix(q, L"pythonioencoding=")) {
                        memcpy(w, q, (l + 1) * sizeof(wchar_t));
                        w += l + 1;
                    }
                    q += l + 1;
                }
                FreeEnvironmentStringsW(parent);
            }
            for (int i = 0; i < 2; i++) {
                size_t l = wcslen(extras[i]);
                memcpy(w, extras[i], (l + 1) * sizeof(wchar_t));
                w += l + 1;
            }
            *w = L'\0';
        }
    }
    return block;
}

/* 按 Windows 标准规则给 argv 元素加引号（兼容反斜杠/引号/空格） */
static wchar_t* build_command_line(const char* const* argv, int argc)
{
    size_t cap = 8;
    wchar_t* cmd;
    wchar_t* w;
    for (int i = 0; i < argc; i++)
        cap += (strlen(argv[i]) + 1) * 2 + 4;
    cmd = (wchar_t*)malloc(cap * sizeof(wchar_t));
    if (!cmd)
        return NULL;
    w = cmd;
    for (int i = 0; i < argc; i++) {
        wchar_t* aw = utf8_to_wide(argv[i]);
        if (!aw) {
            free(cmd);
            return NULL;
        }
        size_t alen = wcslen(aw);
        int needs_quotes = (alen == 0) || wcscspn(aw, L" \t\"") != alen;
        if (i)
            *w++ = L' ';
        if (needs_quotes)
            *w++ = L'"';
        size_t bs = 0;
        for (size_t j = 0;; j++) {
            wchar_t c = aw[j];
            if (c == L'\\') {
                bs++;
                continue;
            }
            int special_end = (c == L'"') || (c == L'\0' && needs_quotes);
            size_t reps = special_end ? bs * 2 : bs;
            for (size_t k = 0; k < reps; k++)
                *w++ = L'\\';
            bs = 0;
            if (c == L'\0')
                break;
            if (c == L'"')
                *w++ = L'"';
            else
                *w++ = c;
        }
        if (needs_quotes)
            *w++ = L'"';
        free(aw);
    }
    *w = L'\0';
    return cmd;
}

static DWORD WINAPI reader_proc(LPVOID arg)
{
    reader_ctx_t* ctx = (reader_ctx_t*)arg;
    ytdlp_process_t* p = ctx->p;
    uint8_t buf[READ_CHUNK];
    size_t cap = 8192, len = 0;
    uint8_t* acc = (uint8_t*)malloc(cap);
    int first = 1;

    if (acc) {
        for (;;) {
            DWORD n = 0;
            if (!ReadFile(ctx->handle, buf, sizeof(buf), &n, NULL) || n == 0)
                break;
            size_t start = 0;
            for (size_t i = 0; i <= n; i++) {
                if (i < n && buf[i] != '\n')
                    continue;
                size_t seg = i - start;
                if (len + seg + 1 > cap) {
                    while (len + seg + 1 > cap)
                        cap *= 2;
                    uint8_t* grown = (uint8_t*)realloc(acc, cap);
                    if (!grown)
                        break;
                    acc = grown;
                }
                memcpy(acc + len, buf + start, seg);
                len += seg;
                start = i + 1;
                if (i == n && (n == 0 || buf[n - 1] != '\n'))
                    break; /* 行未结束，继续累积 */
                size_t dl = len;
                while (dl && (acc[dl - 1] == '\r' || acc[dl - 1] == '\n'))
                    dl--;
                if (first && dl >= 3 && acc[0] == 0xEF && acc[1] == 0xBB && acc[2] == 0xBF) {
                    memmove(acc, acc + 3, dl - 3);
                    dl -= 3;
                }
                if (dl && p->on_line)
                    p->on_line(p->user, acc, (uint32_t)dl, ctx->is_stderr);
                len = 0;
                first = 0;
            }
        }
        if (len) { /* 流结束时残留的最后一行 */
            size_t dl = len;
            while (dl && acc[dl - 1] == '\r')
                dl--;
            if (dl && p->on_line)
                p->on_line(p->user, acc, (uint32_t)dl, ctx->is_stderr);
        }
    }
    free(acc);
    free(ctx);
    proc_release(p);
    return 0;
}

static DWORD WINAPI monitor_proc(LPVOID arg)
{
    ytdlp_process_t* p = (ytdlp_process_t*)arg;
    HANDLE handles[2];
    int n = 0;

    if (p->stdout_thread)
        handles[n++] = p->stdout_thread;
    if (p->stderr_thread)
        handles[n++] = p->stderr_thread;
    if (n)
        WaitForMultipleObjects((DWORD)n, handles, TRUE, INFINITE);

    if (p->process_handle) {
        DWORD c = 0;
        if (!GetExitCodeProcess(p->process_handle, &c))
            c = 0xFFFFFFFFu;
        InterlockedExchange(&p->exit_code, (LONG)c);
    }
    if (InterlockedCompareExchange(&p->exit_fired, 1, 0) == 0 && p->on_exit)
        p->on_exit(p->user, (uint32_t)InterlockedCompareExchange(&p->exit_code, 0, 0));
    proc_release(p);
    return 0;
}

YTDLP_API int32_t ytdlp_host_spawn(const ytdlp_spawn_info* info, void** out_handle)
{
    if (!info || !out_handle || !info->exe_path_utf8 || !info->argv_utf8 || info->argc < 1)
        return -1;
    *out_handle = NULL;

    ytdlp_process_t* p = (ytdlp_process_t*)calloc(1, sizeof(ytdlp_process_t));
    if (!p)
        return -2;
    InitializeCriticalSection(&p->lock);
    p->ref_count = 1;
    p->on_line = info->on_line;
    p->on_exit = info->on_exit;
    p->user = info->user;

    wchar_t* exe_w = utf8_to_wide(info->exe_path_utf8);
    wchar_t* cmd_w = build_command_line(info->argv_utf8, info->argc);
    wchar_t* cwd_w = info->working_dir_utf8 ? utf8_to_wide(info->working_dir_utf8) : NULL;
    wchar_t* env_w = build_env_block();

    int rc = -3;
    HANDLE stdout_w = NULL, stderr_w = NULL, nul_handle = INVALID_HANDLE_VALUE;
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;

    if (exe_w && cmd_w && env_w) {
        p->job_handle = CreateJobObjectW(NULL, NULL);
        if (p->job_handle) {
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION jl;
            ZeroMemory(&jl, sizeof(jl));
            jl.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            SetInformationJobObject(p->job_handle, JobObjectExtendedLimitInformation, &jl, sizeof(jl));
        }
        SECURITY_ATTRIBUTES sa = { sizeof(SECURITY_ATTRIBUTES), NULL, TRUE };
        BOOL pipes_ok =
            CreatePipe(&p->stdout_read, &stdout_w, &sa, PIPE_BUF_SIZE) &&
            CreatePipe(&p->stderr_read, &stderr_w, &sa, PIPE_BUF_SIZE) &&
            SetHandleInformation(p->stdout_read, HANDLE_FLAG_INHERIT, 0) &&
            SetHandleInformation(p->stderr_read, HANDLE_FLAG_INHERIT, 0);
        nul_handle = CreateFileW(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                 &sa, OPEN_EXISTING, 0, NULL);
        if (pipes_ok && p->job_handle && nul_handle != INVALID_HANDLE_VALUE) {
            ZeroMemory(&si, sizeof(si));
            ZeroMemory(&pi, sizeof(pi));
            si.cb = sizeof(si);
            si.dwFlags = STARTF_USESTDHANDLES;
            si.hStdInput = nul_handle; /* 送到 NUL：子进程读 stdin 立即得到 EOF */
            si.hStdOutput = stdout_w;
            si.hStdError = stderr_w;
            if (CreateProcessW(exe_w, cmd_w, NULL, NULL, TRUE,
                               CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | CREATE_NO_WINDOW,
                               env_w, cwd_w, &si, &pi)) {
                /* 挂起态先入 Job 再恢复，杜绝子进程逃逸窗口 */
                AssignProcessToJobObject(p->job_handle, pi.hProcess);
                ResumeThread(pi.hThread);
                CloseHandle(pi.hThread);
                p->process_handle = pi.hProcess;
                rc = 0;
            }
            else {
                rc = -5;
            }
        }
        else {
            rc = -4;
        }
    }

    if (stdout_w)
        CloseHandle(stdout_w);
    if (stderr_w)
        CloseHandle(stderr_w);
    if (nul_handle != INVALID_HANDLE_VALUE && nul_handle)
        CloseHandle(nul_handle);

    if (rc == 0) {
        int ok = 1;
        for (int s = 0; s < 2 && ok; s++) {
            reader_ctx_t* c = (reader_ctx_t*)malloc(sizeof(reader_ctx_t));
            if (!c) {
                ok = 0;
                break;
            }
            c->p = p;
            c->handle = s ? p->stderr_read : p->stdout_read;
            c->is_stderr = s;
            InterlockedIncrement(&p->ref_count);
            HANDLE th = CreateThread(NULL, 0, reader_proc, c, 0, NULL);
            if (!th) {
                proc_release(p);
                free(c);
                ok = 0;
                break;
            }
            if (s)
                p->stderr_thread = th;
            else
                p->stdout_thread = th;
        }
        if (ok) {
            InterlockedIncrement(&p->ref_count);
            if (CreateThread(NULL, 0, monitor_proc, p, 0, NULL)) {
                *out_handle = p;
                rc = 0;
            }
            else {
                proc_release(p);
                rc = -6;
            }
        }
        else {
            rc = -6;
        }
    }

    free(exe_w);
    free(cmd_w);
    free(cwd_w);
    free(env_w);

    if (rc != 0) {
        if (p->job_handle)
            TerminateJobObject(p->job_handle, 1);
        proc_release(p); /* 若读线程已启动，它们会各自归还引用并最终释放 */
        return rc;
    }
    return 0;
}

YTDLP_API int32_t ytdlp_host_cancel(void* handle)
{
    ytdlp_process_t* p = (ytdlp_process_t*)handle;
    if (!p)
        return -1;
    if (p->job_handle)
        TerminateJobObject(p->job_handle, CANCEL_EXIT_CODE);
    return 0;
}

YTDLP_API int32_t ytdlp_host_release(void* handle)
{
    ytdlp_process_t* p = (ytdlp_process_t*)handle;
    if (!p)
        return -1;
    proc_release(p);
    return 0;
}

YTDLP_API int32_t ytdlp_host_get_exit_code(void* handle, uint32_t* out_code)
{
    ytdlp_process_t* p = (ytdlp_process_t*)handle;
    if (!p || !out_code)
        return -1;
    *out_code = (uint32_t)InterlockedCompareExchange(&p->exit_code, 0, 0);
    return 0;
}

YTDLP_API const char* ytdlp_host_version(void)
{
    return YTDLP_HOST_VERSION;
}
