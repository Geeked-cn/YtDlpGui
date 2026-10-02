using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace YtDlpGui.Services;

/// <summary>定位 / 下载 yt-dlp.exe 与 ffmpeg.exe。</summary>
public static class ToolLocator
{
    public static readonly string ToolsDir = Path.Combine(AppContext.BaseDirectory, "tools");

    public static string? FindYtDlp()
    {
        string local = Path.Combine(ToolsDir, "yt-dlp.exe");
        if (File.Exists(local)) return local;
        return FindOnPath("yt-dlp.exe");
    }

    public static string? FindFfmpeg()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(ToolsDir, "ffmpeg.exe"),
                     Path.Combine(ToolsDir, "ffmpeg", "bin", "ffmpeg.exe"),
                     Path.Combine(ToolsDir, "ffmpeg", "ffmpeg.exe"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return FindOnPath("ffmpeg.exe");
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string full = Path.Combine(dir, exe);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // PATH 中可能有非法目录，跳过
            }
        }
        return null;
    }

    /// <summary>下载 yt-dlp.exe（GitHub latest，或经镜像前缀加速）。返回新路径。</summary>
    public static async Task<string> DownloadYtDlpAsync(string mirrorPrefix, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        const string github = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        string url = string.IsNullOrWhiteSpace(mirrorPrefix)
            ? github
            : mirrorPrefix.TrimEnd('/') + "/" + github;
        string dest = Path.Combine(ToolsDir, "yt-dlp.exe");
        await Http.DownloadAsync(url, dest, progress, ct).ConfigureAwait(false);
        return dest;
    }

    /// <summary>下载 ffmpeg essentials 发行包并解出 ffmpeg/ffprobe 到 tools 目录。</summary>
    public static async Task<string> DownloadFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        string zipPath = Path.Combine(Path.GetTempPath(), "ffmpeg-essentials.zip");
        await Http.DownloadAsync("https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip", zipPath, progress, ct)
            .ConfigureAwait(false);

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            string name = Path.GetFileName(entry.FullName);
            if (name is not ("ffmpeg.exe" or "ffprobe.exe")) continue;
            string dest = Path.Combine(ToolsDir, name);
            entry.ExtractToFile(dest, overwrite: true);
        }
        try { File.Delete(zipPath); } catch { /* 临时文件删除失败无所谓 */ }
        return Path.Combine(ToolsDir, "ffmpeg.exe");
    }
}

/// <summary>共享 HttpClient + 带进度的大文件下载。</summary>
internal static class Http
{
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    static Http()
    {
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("YtDlpGui/1.0 (+https://github.com/yt-dlp/yt-dlp)");
    }

    public static async Task DownloadAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);
        var buf = new byte[1 << 16];
        long done = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = -1;
        int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (progress != null && sw.ElapsedMilliseconds - lastReport > 200)
            {
                lastReport = sw.ElapsedMilliseconds;
                progress.Report(total is > 0 ? done * 100.0 / total.Value : 0);
            }
        }
        progress?.Report(100);
    }
}
