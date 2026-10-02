#ifndef YTDLP_HOST_H
#define YTDLP_HOST_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifdef YTDLP_HOST_BUILD
#define YTDLP_API __declspec(dllexport)
#else
#define YTDLP_API __declspec(dllimport)
#endif

/*
 * ytdlp_host —— yt-dlp.exe 进程宿主（原生层）
 *
 * 设计目标：
 *   1. 事件驱动：阻塞式管道读（零轮询、零 CPU 空转），每产生一行 UTF-8 输出回调一次；
 *   2. 进程树管控：子进程（ffmpeg 等）放入 Job Object，取消时整树终止，应用退出不残留；
 *   3. UTF-8 全链路：命令行/环境/输出均 UTF-8，原生层不做编码转换（转码交给托管层一次性完成）；
 *   4. 安全传参：argv 数组跨越边界，原生层按 Windows 标准规则加引号，避免命令注入。
 *
 * 回调约定：on_line / on_exit 在原生线程上被调用，实现方必须自行保证线程安全，
 * 且不得在回调中长期阻塞（会推迟该管道的后续读取）。
 *
 * spawn 之后原生层立即完成参数拷贝，托管层可在 spawn 返回后释放传入的内存。
 */

typedef void (*ytdlp_line_cb)(void* user, const uint8_t* data_utf8, uint32_t len, int is_stderr);
typedef void (*ytdlp_exit_cb)(void* user, uint32_t exit_code);

typedef struct {
    const char* exe_path_utf8;        /* 可执行文件完整路径 */
    const char* const* argv_utf8;     /* argv[0] 为程序名，其后为原始参数（不需要引号） */
    int32_t argc;                     /* argv 元素个数 */
    const char* working_dir_utf8;     /* 工作目录，可为 NULL */
    ytdlp_line_cb on_line;            /* 每行输出回调（stdout/stderr），可为 NULL */
    ytdlp_exit_cb on_exit;            /* 进程退出回调（恰好触发一次，晚于全部行回调），可为 NULL */
    void* user;                       /* 透传给回调的上下文 */
} ytdlp_spawn_info;

/* 返回 0 表示成功，负数为错误码。out_handle 收到句柄后必须用 ytdlp_host_release 释放。 */
YTDLP_API int32_t ytdlp_host_spawn(const ytdlp_spawn_info* info, void** out_handle);

/* 终止整棵进程树（Job Object）。幂等，进程已退出时返回 0。 */
YTDLP_API int32_t ytdlp_host_cancel(void* handle);

/* 归还托管方持有的引用。句柄在所有内部线程结束后自动释放，调用后不得再使用。 */
YTDLP_API int32_t ytdlp_host_release(void* handle);

/* 读取退出码；未退出时为 STILL_ACTIVE(259)。 */
YTDLP_API int32_t ytdlp_host_get_exit_code(void* handle, uint32_t* out_code);

YTDLP_API const char* ytdlp_host_version(void);

#ifdef __cplusplus
}
#endif

#endif /* YTDLP_HOST_H */
