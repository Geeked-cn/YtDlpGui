using System;
using System.IO;

namespace YtDlpGui.Services;

/// <summary>启动阶段文件日志：GUI 进程无法看 stderr，崩溃排查靠它。</summary>
public static class BootLog
{
    private static readonly object Sync = new();
    private static readonly string Path =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YtDlpGui", "boot.log");

    public static void Write(string stage, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            lock (Sync)
            {
                File.AppendAllText(Path,
                    $"{DateTime.Now:HH:mm:ss.fff} [{System.Threading.Thread.CurrentThread.ManagedThreadId}] {stage}" +
                    (ex == null ? "" : $" EXC: {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}") + "\n");
            }
        }
        catch
        {
            // 日志失败不影响运行
        }
    }
}
