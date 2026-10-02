using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YtDlpGui.Models;

namespace YtDlpGui.Services;

public sealed record QualityOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record ContainerOption(string Id, string Label)
{
    public override string ToString() => Label;
}

public static class Catalog
{
    public static readonly QualityOption[] Quality =
    {
        new("best", "最佳画质"),
        new("1080", "≤ 1080P"),
        new("720", "≤ 720P"),
        new("audio", "仅音频 (MP3)"),
    };

    public static readonly ContainerOption[] Containers =
    {
        new("mp4", "MP4 兼容"),
        new("mkv", "MKV 无损"),
    };

    /// <summary>GitHub 直连 / 镜像前缀（仅作用于 github.com 资源）。</summary>
    public static readonly string[] Mirrors =
    {
        "",
        "https://ghproxy.cn",
        "https://gh-proxy.com",
    };
}

public sealed record ProbeInfo(string DisplayTitle, string Subtitle);

public sealed record DownloadOptions(
    string OutputDir,
    string Quality,
    string Container,
    bool WholePlaylist,
    bool EmbedMetadata,
    string? CookieSpec);

/// <summary>
/// 封装对 yt-dlp.exe 的调用（不重写、直接引用）：元数据探测 (-J) 与下载 (-f 最佳画质合成)。
/// 输出全部走 --newline + --progress-template 机器可读格式，配合哨兵前缀解析。
/// </summary>
public sealed class DownloadService
{
    /// <summary>DASH/HLS 分片并发数，显著提升高清视频下载速度。</summary>
    private const int ConcurrentFragments = 8;

    private readonly string _ytDlpPath;
    private readonly string? _ffmpegPath;

    public DownloadService(string ytDlpPath, string? ffmpegPath)
    {
        _ytDlpPath = ytDlpPath;
        _ffmpegPath = ffmpegPath;
    }

    // ---------- 元数据探测 ----------

    public async Task<ProbeInfo> ProbeAsync(string url, string? cookieSpec, CancellationToken ct)
    {
        var a = new List<string> { "--ignore-config", "--no-warnings", "--encoding", "utf-8", "-J", "--playlist-items", "1" };
        if (!string.IsNullOrEmpty(cookieSpec))
            a.AddRange(new[] { "--cookies-from-browser", cookieSpec });
        a.Add(url);

        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(180));
        var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

        using (var proc = NativeProcess.Start(_ytDlpPath, a,
                   (s, isErr) => (isErr ? stderr : stdout).Enqueue(s),
                   code => done.TrySetResult(code)))
        using (cts.Token.Register(static p => ((NativeProcess)p!).Cancel(), proc))
        {
            await done.Task.ConfigureAwait(false);
        }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (cts.IsCancellationRequested)
            throw new TimeoutException("获取视频信息超时");

        string text = string.Join("\n", stdout);
        int brace = text.IndexOf('{');
        if (brace < 0)
        {
            string err = stderr.FirstOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal))
                         ?? stderr.FirstOrDefault()
                         ?? "无法获取视频信息（网络问题或 Cookies 失效）";
            throw new InvalidOperationException(err);
        }

        try
        {
            using var doc = JsonDocument.Parse(text[brace..]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException("顶层不是对象");

            bool isPlaylist = root.TryGetProperty("_type", out var typeEl)
                              && typeEl.ValueKind == JsonValueKind.String
                              && typeEl.GetString() == "playlist";
            if (isPlaylist)
            {
                string title = GetString(root, "title") ?? "播放列表";
                int count = 0;
                if (root.TryGetProperty("playlist_count", out var cnt) && cnt.ValueKind == JsonValueKind.Number)
                    count = cnt.GetInt32();
                else if (root.TryGetProperty("entries", out var en) && en.ValueKind == JsonValueKind.Array)
                    count = en.GetArrayLength();
                return new ProbeInfo(title, $"播放列表 · 共 {count} 个");
            }

            string t = GetString(root, "title") ?? url;
            var bits = new List<string>(2);
            string? x = GetString(root, "extractor_key");
            if (!string.IsNullOrEmpty(x)) bits.Add(x);
            string? d = GetString(root, "duration_string");
            if (!string.IsNullOrEmpty(d)) bits.Add(d);
            return new ProbeInfo(t, string.Join(" · ", bits));
        }
        catch (JsonException je)
        {
            throw new InvalidOperationException("解析视频信息失败: " + je.Message);
        }
    }

    // ---------- 下载 ----------

    public NativeProcess StartDownload(DownloadTask task, DownloadOptions o,
        Action<string, bool> onLine, Action<uint> onExit)
    {
        var a = new List<string>
        {
            "--ignore-config",
            "--encoding", "utf-8", // yt-dlp 会按本地代码页写管道（中文系统为 GBK），强制 UTF-8
            "--newline", "--no-quiet", // --print 会隐式启用 quiet，这里强制关闭，保留 [Merger] 等日志
            "--progress-template",
            "download:__PROG__|%(progress._percent_str)s|%(progress._downloaded_bytes_str)s|%(progress._total_bytes_estimate_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
            "--print", "after_move:__FILE__%(filepath)s",
            "--windows-filenames",
            "-N", ConcurrentFragments.ToString(),
            "-o", Path.Combine(o.OutputDir, "%(title)s [%(id)s].%(ext)s"),
        };

        if (_ffmpegPath is not null)
            a.AddRange(new[] { "--ffmpeg-location", _ffmpegPath });

        switch (o.Quality)
        {
            case "1080":
                a.AddRange(new[]
                {
                    "-f", "bv*[ext=mp4][height<=1080]+ba[ext=m4a]/bv*[height<=1080]+ba/b[height<=1080]/b",
                    "--merge-output-format", "mp4",
                });
                break;
            case "720":
                a.AddRange(new[]
                {
                    "-f", "bv*[ext=mp4][height<=720]+ba[ext=m4a]/bv*[height<=720]+ba/b[height<=720]/b",
                    "--merge-output-format", "mp4",
                });
                break;
            case "audio":
                a.AddRange(new[] { "-x", "--audio-format", "mp3", "--audio-quality", "0" });
                break;
            default: // best
                if (o.Container == "mkv")
                    a.AddRange(new[] { "-f", "bv*+ba/b", "--merge-output-format", "mkv" });
                else
                    a.AddRange(new[] { "-f", "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b", "--merge-output-format", "mp4" });
                break;
        }

        if (!string.IsNullOrEmpty(o.CookieSpec))
            a.AddRange(new[] { "--cookies-from-browser", o.CookieSpec });
        a.Add(o.WholePlaylist ? "--yes-playlist" : "--no-playlist");
        if (o.EmbedMetadata)
            a.Add("--embed-metadata");
        a.Add(task.Url);

        return NativeProcess.Start(_ytDlpPath, a, onLine, onExit, o.OutputDir);
    }

    // ---------- 行解析 ----------

    public static void ParseLine(DownloadTask t, string line)
    {
        if (line.Length == 0) return;

        if (line.StartsWith("__PROG__|", StringComparison.Ordinal))
        {
            var parts = line.Split('|');
            double pct = -1;
            if (parts.Length > 1)
            {
                string s = parts[1].Trim().TrimEnd('%');
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    pct = v;
            }
            t.SetProgress(pct, Get(parts, 2), Get(parts, 3), Get(parts, 4), Get(parts, 5));
            return;
        }

        int fi = line.IndexOf("__FILE__", StringComparison.Ordinal);
        if (fi >= 0)
        {
            t.SetFile(line[(fi + 8)..].Trim());
            return;
        }

        const string itemMark = "Downloading item ";
        int ii = line.IndexOf(itemMark, StringComparison.Ordinal);
        if (ii >= 0)
        {
            int a = ii + itemMark.Length;
            int b = line.IndexOf(" of ", a, StringComparison.Ordinal);
            if (b > a)
                t.SetInfo($"列表第 {line[a..b].Trim()} 个");
            return;
        }

        if (line.StartsWith("[Merger]", StringComparison.Ordinal)
            || line.StartsWith("[ExtractAudio]", StringComparison.Ordinal)
            || line.StartsWith("[Metadata]", StringComparison.Ordinal)
            || line.StartsWith("[EmbedThumbnail]", StringComparison.Ordinal)
            || line.StartsWith("[VideoRemux]", StringComparison.Ordinal)
            || line.StartsWith("[FixupM3u8]", StringComparison.Ordinal))
        {
            t.SetInfo(line);
            return;
        }

        if (line.StartsWith("ERROR", StringComparison.Ordinal))
            t.AddError(line);
    }

    public static bool LooksLikeCookieIssue(string msg)
    {
        return msg.Contains("cookie", StringComparison.OrdinalIgnoreCase)
               || msg.Contains("decrypt", StringComparison.OrdinalIgnoreCase);
    }

    private static string Get(string[] p, int i) => i < p.Length ? p[i].Trim() : "";

    private static string? GetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
