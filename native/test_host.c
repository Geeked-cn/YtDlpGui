#include <stdio.h>
#include <string.h>
#include <windows.h>
#include "ytdlp_host.h"

static void on_line(void* user, const uint8_t* data, uint32_t len, int is_stderr)
{
    char buf[4096];
    uint32_t n = len < sizeof(buf) - 1 ? len : (uint32_t)sizeof(buf) - 1;
    memcpy(buf, data, n);
    buf[n] = 0;
    printf("  [%s] %s\n", is_stderr ? "E" : "O", buf);
    fflush(stdout);
}

static LONG g_done = 0;
static DWORD g_exit = 0xDEADBEEF;

static void on_exit(void* user, uint32_t code)
{
    g_exit = code;
    InterlockedExchange(&g_done, 1);
}

static int run(const char* exe, const char* const* argv, int argc, int timeout_ms)
{
    void* h = NULL;
    g_done = 0;
    g_exit = 0xDEADBEEF;
    ytdlp_spawn_info info = {0};
    info.exe_path_utf8 = exe;
    info.argv_utf8 = argv;
    info.argc = argc;
    info.on_line = on_line;
    info.on_exit = on_exit;
    DWORD t0 = GetTickCount();
    int rc = ytdlp_host_spawn(&info, &h);
    printf("spawn rc=%d\n", rc);
    if (rc != 0) return 1;
    while (!InterlockedCompareExchange(&g_done, 0, 0)) {
        if (GetTickCount() - t0 > (DWORD)timeout_ms) {
            printf("TIMEOUT after %d ms\n", timeout_ms);
            ytdlp_host_cancel(h);
            Sleep(500);
            ytdlp_host_release(h);
            return 2;
        }
        Sleep(20);
    }
    printf("exit_code=%u in %lu ms\n", g_exit, GetTickCount() - t0);
    ytdlp_host_release(h);
    return 0;
}

int main(void)
{
    printf("host version: %s\n", ytdlp_host_version());

    puts("== test 1: cmd /c echo hello ==");
    const char* cmd = "C:\\Windows\\System32\\cmd.exe";
    const char* argv1[] = { cmd, "/c", "echo", "hello-native" };
    int r1 = run(cmd, argv1, 4, 10000);

    puts("== test 2: yt-dlp --version ==");
    const char* ytdlp = "C:\\Windows\\System32\\yt-dlp.exe";
    const char* argv2[] = { ytdlp, "--version" };
    int r2 = run(ytdlp, argv2, 2, 30000);

    puts("== test 3: cancel cmd ping ==");
    const char* argv3[] = { cmd, "/c", "ping", "-n", "20", "127.0.0.1" };
    void* h = NULL;
    ytdlp_spawn_info info = {0};
    info.exe_path_utf8 = cmd;
    info.argv_utf8 = argv3;
    info.argc = 6;
    info.on_exit = on_exit;
    DWORD t0 = GetTickCount();
    int rc = ytdlp_host_spawn(&info, &h);
    printf("spawn rc=%d\n", rc);
    Sleep(700);
    ytdlp_host_cancel(h);
    while (!InterlockedCompareExchange(&g_done, 0, 0) && GetTickCount() - t0 < 8000)
        Sleep(20);
    printf("cancel exit=%u in %lu ms -> %s\n", g_exit, GetTickCount() - t0,
           g_done ? "OK" : "TIMEOUT");
    ytdlp_host_release(h);

    return (r1 || r2) ? 1 : 0;
}
