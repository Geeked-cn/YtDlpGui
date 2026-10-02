using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using YtDlpGui.Services;

// ytdlp_host.dll 冒烟测试：
//   1) 进程启动 + stdout 行回调（优先用真实 yt-dlp --version）
//   2) UTF-8 中文经管道往返无损
//   3) Job Object 一键终止整棵进程树
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine($"ytdlp_host 版本: {NativeProcess.NativeVersion}");
        string? ytDlp = ToolLocator.FindYtDlp();
        Console.WriteLine($"yt-dlp: {ytDlp ?? "未找到(用 cmd 替代)"}");

        RunCmdTest().GetAwaiter().GetResult();
        RunFfmpegTest(ToolLocator.FindFfmpeg()).GetAwaiter().GetResult();
        RunEchoTest(ytDlp).GetAwaiter().GetResult();
        RunUtf8Test().GetAwaiter().GetResult();
        RunCancelTest().GetAwaiter().GetResult();

        Console.WriteLine(_failures == 0 ? "== 全部通过 ==" : $"== 失败 {_failures} 项 ==");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunEchoTest(string? ytDlp)
    {
        string exe;
        string[] args;
        if (ytDlp is not null)
        {
            exe = ytDlp;
            args = new[] { "--version" };
        }
        else
        {
            exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            args = new[] { "/c", "echo", "smoke-ok" };
        }

        var lines = new List<string>();
        var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        using (var p = NativeProcess.Start(exe, args,
                   (s, isErr) => { if (!isErr) lock (lines) lines.Add(s); },
                   c => done.TrySetResult(c)))
        {
            uint code = await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
            sw.Stop();
            string joined = string.Join("\n", lines);
            bool pass = code == 0 &&
                        (ytDlp is not null
                            ? Regex.IsMatch(joined, @"^\d{4}\.\d{2}\.\d{2}", RegexOptions.Multiline)
                            : joined.Contains("smoke-ok"));
            Report("进程启动 + stdout 行回调", pass,
                $"exit={code}, {sw.ElapsedMilliseconds}ms, 输出=[{joined.ReplaceLineEndings(" | ")}]");
        }
    }

    private static async Task RunCmdTest()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var lines = new List<string>();
        var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var p = NativeProcess.Start(exe, new[] { "/c", "echo", "hello-csharp" },
                   (s, isErr) => { if (!isErr) lock (lines) lines.Add(s); },
                   c => done.TrySetResult(c)))
        {
            uint code = await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            string joined = string.Join("\n", lines);
            Report("P/Invoke 链路 (cmd echo)", code == 0 && joined.Contains("hello-csharp"),
                $"exit={code}, 输出=[{joined.ReplaceLineEndings(" | ")}]");
        }
    }

    private static async Task RunFfmpegTest(string? ffmpeg)
    {
        if (ffmpeg is null)
        {
            Console.WriteLine("[跳过] ffmpeg 未找到");
            return;
        }
        var lines = new List<string>();
        var errs = new List<string>();
        var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        using (var p = NativeProcess.Start(ffmpeg, new[] { "-version" },
                   (s, isErr) => { lock (lines) { (isErr ? errs : lines).Add(s); } },
                   c => done.TrySetResult(c)))
        {
            uint code = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            string first = lines.Count > 0 ? lines[0] : "(无输出)";
            Report("ffmpeg -version", code == 0 && first.Contains("ffmpeg"),
                $"exit={code}, {sw.ElapsedMilliseconds}ms, out=[{first.Trim()}], err=[{string.Join(" | ", errs).Trim()}]");
        }
    }

    private static async Task RunUtf8Test()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(exe))
        {
            Report("UTF-8 行传输", true, "跳过（无 powershell）");
            return;
        }

        var lines = new List<string>();
        var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var p = NativeProcess.Start(exe,
                   new[]
                   {
                       "-NoProfile", "-Command",
                       "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Write-Output '中文-标题-测试'",
                   },
                   (s, isErr) => { if (!isErr) lock (lines) lines.Add(s); },
                   c => done.TrySetResult(c)))
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
            string joined = string.Join("\n", lines);
            Report("UTF-8 行传输", joined.Contains("中文-标题-测试"), joined.ReplaceLineEndings(" | "));
        }
    }

    private static async Task RunCancelTest()
    {
        string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();
        using (var p = NativeProcess.Start(exe, new[] { "/c", "ping", "-n", "20", "127.0.0.1" },
                   null, c => done.TrySetResult(c)))
        {
            await Task.Delay(600);
            bool cancelOk = p.Cancel();
            uint code = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
            sw.Stop();
            Report("Job 进程树终止", cancelOk && sw.ElapsedMilliseconds < 9000,
                $"cancel={cancelOk}, exit=0x{code:X}, {sw.ElapsedMilliseconds}ms 内退出");
        }
    }

    private static void Report(string name, bool pass, string detail)
    {
        if (!pass) _failures++;
        Console.WriteLine($"[{(pass ? "通过" : "失败")}] {name} — {detail}");
    }
}
