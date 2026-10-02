using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using YtDlpGui.Models;
using YtDlpGui.Services;

namespace YtDlpGui.ViewModels;

/// <summary>确定性并发闸门（无轮询）：限制同时运行的 yt-dlp 进程数，支持运行中调整上限。</summary>
public sealed class Gate
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource> _waiters = new();
    private int _count;
    private int _max;

    public Gate(int max) => _max = Math.Max(1, max);

    public void SetMax(int max)
    {
        lock (_lock)
        {
            _max = Math.Max(1, max);
            Pump();
        }
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        TaskCompletionSource tcs;
        lock (_lock)
        {
            if (_count < _max)
            {
                _count++;
                return;
            }
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(tcs);
        }
        await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    public void Release()
    {
        lock (_lock)
        {
            _count--;
            Pump();
        }
    }

    private void Pump()
    {
        while (_count < _max && _waiters.TryDequeue(out var w))
        {
            _count++;
            w.TrySetResult();
        }
    }
}

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dq;
    private readonly Logger _log = new();
    private Gate? _gate;
    private DownloadService? _svc;
    private readonly AppSettings _settings;

    public ObservableCollection<DownloadTask> Tasks { get; } = new();
    public ObservableCollection<QualityOption> QualityOptions { get; } = new(Catalog.Quality);
    public ObservableCollection<ContainerOption> ContainerOptions { get; } = new(Catalog.Containers);
    public ObservableCollection<BrowserOption> BrowserOptions { get; } = new();
    public Logger Log => _log;

    [ObservableProperty] private string _urlInput = "";
    [ObservableProperty] private QualityOption _selectedQuality;
    [ObservableProperty] private ContainerOption _selectedContainer;
    [ObservableProperty] private BrowserOption _selectedBrowser = BrowserOption.None;
    [ObservableProperty] private bool _useCookies = true;
    [ObservableProperty] private bool _wholePlaylist;
    [ObservableProperty] private bool _embedMetadata = true;
    [ObservableProperty] private string _outputDir;
    [ObservableProperty] private int _concurrent = 2;
    [ObservableProperty] private bool _autoDownloadTools = true;
    [ObservableProperty] private int _mirrorIndex;
    [ObservableProperty] private string _toolStatus = "正在初始化…";

    partial void OnConcurrentChanged(int value) => _gate?.SetMax(value);

    public string? YtDlpPath { get; private set; }
    public string? FfmpegPath { get; private set; }

    public string Mirror => Catalog.Mirrors[Math.Clamp(MirrorIndex, 0, Catalog.Mirrors.Length - 1)];

    public MainViewModel(DispatcherQueue dq)
    {
        _dq = dq;
        _settings = SettingsService.Load();
        _outputDir = _settings.OutputDir;
        _concurrent = Math.Clamp(_settings.Concurrent, 1, 4);
        _autoDownloadTools = _settings.AutoDownloadTools;
        _mirrorIndex = Math.Clamp(_settings.MirrorIndex, 0, Catalog.Mirrors.Length - 1);
        _embedMetadata = _settings.EmbedMetadata;
        _wholePlaylist = _settings.WholePlaylist;
        _useCookies = _settings.UseCookies;
        _selectedQuality = Catalog.Quality.FirstOrDefault(q => q.Id == _settings.Quality) ?? Catalog.Quality[0];
        _selectedContainer = Catalog.Containers.FirstOrDefault(c => c.Id == _settings.Container) ?? Catalog.Containers[0];
        if (!string.IsNullOrEmpty(_settings.Browser))
            _selectedBrowser = new BrowserOption(_settings.Browser, _settings.Browser);
    }

    private void Ui(Action a)
    {
        if (_dq.HasThreadAccess) a();
        else _dq.TryEnqueue(() => a());
    }

    // ---------- 初始化 ----------

    public Task LoadAsync()
    {
        // 浏览器目录探测是几个 Directory.Exists，直接在调用线程（UI）同步做，
        // 避免 Task.Run + TryEnqueue 的回调晚到覆盖后续状态
        YtDlpPath = ToolLocator.FindYtDlp();
        FfmpegPath = ToolLocator.FindFfmpeg();
        var detected = CookieService.Detect();

        BrowserOptions.Add(BrowserOption.None);
        foreach (var b in detected)
            BrowserOptions.Add(b);

        var wanted = BrowserOptions.FirstOrDefault(b => b.Id == _settings.Browser && b.Id.Length > 0);
        SelectedBrowser = wanted
            ?? (UseCookies && detected.Count > 0 ? detected[0] : BrowserOption.None);

        ToolStatus = YtDlpPath is null
            ? "未找到 yt-dlp.exe"
            : $"找到 {Path.GetFileName(YtDlpPath)} · " +
              (FfmpegPath is null ? "缺少 ffmpeg" : "ffmpeg 就绪");
        return Task.CompletedTask;
    }

    public void CreateService()
    {
        if (YtDlpPath is null) return;
        _svc = new DownloadService(YtDlpPath, FfmpegPath);
        _gate ??= new Gate(Concurrent);
        Ui(() => ToolStatus = $"就绪 · {Path.GetFileName(YtDlpPath)} · " +
            (FfmpegPath is null ? "缺少 ffmpeg（无法合成高清）" : "ffmpeg 就绪") +
            $" · ytdlp_host {NativeProcess.NativeVersion}");
    }

    public async Task<bool> InstallYtDlpAsync()
    {
        try
        {
            var progress = new Progress<double>(d => Ui(() => ToolStatus = $"正在下载 yt-dlp… {d:0}%"));
            YtDlpPath = await ToolLocator.DownloadYtDlpAsync(Mirror, progress, CancellationToken.None).ConfigureAwait(false);
            CreateService();
            return true;
        }
        catch (Exception ex)
        {
            Ui(() => ToolStatus = "yt-dlp 下载失败：" + ex.Message + "（可在设置中切换镜像源）");
            return false;
        }
    }

    public async Task<bool> InstallFfmpegAsync()
    {
        try
        {
            var progress = new Progress<double>(d => Ui(() => ToolStatus = $"正在下载 ffmpeg… {d:0}%（约 90MB）"));
            FfmpegPath = await ToolLocator.DownloadFfmpegAsync(progress, CancellationToken.None).ConfigureAwait(false);
            CreateService();
            return true;
        }
        catch (Exception ex)
        {
            Ui(() => ToolStatus = "ffmpeg 下载失败：" + ex.Message);
            return false;
        }
    }

    public void SaveSettings()
    {
        _settings.OutputDir = OutputDir;
        _settings.Concurrent = Concurrent;
        _settings.AutoDownloadTools = AutoDownloadTools;
        _settings.MirrorIndex = MirrorIndex;
        _settings.EmbedMetadata = EmbedMetadata;
        _settings.WholePlaylist = WholePlaylist;
        _settings.UseCookies = UseCookies;
        _settings.Quality = SelectedQuality.Id;
        _settings.Container = SelectedContainer.Id;
        _settings.Browser = SelectedBrowser.Id;
        SettingsService.Save(_settings);
    }

    // ---------- 任务调度 ----------

    public async Task StartFromInputAsync()
    {
        List<string> urls = SplitUrls(UrlInput);
        if (urls.Count == 0) return;
        if (_svc is null)
        {
            ToolStatus = "下载组件未就绪，请先完成初始化";
            return;
        }
        UrlInput = "";
        foreach (string u in urls)
        {
            var t = new DownloadTask(u);
            Tasks.Insert(0, t);
            _ = Task.Run(() => RunAsync(t));
        }
        await Task.CompletedTask;
    }

    private static List<string> SplitUrls(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new List<string>();
        return input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Contains('.', StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task RunAsync(DownloadTask t)
    {
        Gate? gate = _gate;
        DownloadService? svc = _svc;
        if (gate is null || svc is null)
        {
            Ui(() =>
            {
                t.SetPhase(TaskPhase.Failed);
                t.StatusText = "服务未初始化";
            });
            return;
        }

        try
        {
            await gate.WaitAsync(t.Cts.Token).ConfigureAwait(false);
            try
            {
                string? cookie = UseCookies && SelectedBrowser.Id.Length > 0 ? SelectedBrowser.Id : null;
                var opts = new DownloadOptions(OutputDir, SelectedQuality.Id, SelectedContainer.Id,
                    WholePlaylist, EmbedMetadata, cookie);

                Ui(() => t.SetPhase(TaskPhase.Probing));
                try
                {
                    ProbeInfo info = await svc.ProbeAsync(t.Url, cookie, t.Cts.Token).ConfigureAwait(false);
                    Ui(() =>
                    {
                        t.Title = info.DisplayTitle;
                        t.StatusText = info.Subtitle;
                    });
                }
                catch (OperationCanceledException) when (t.CancelRequested) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.Add("[probe] " + ex.Message);
                    Ui(() =>
                    {
                        t.Title = t.Url;
                        t.StatusText = "信息获取失败，直接尝试下载";
                    });
                }

                t.Cts.Token.ThrowIfCancellationRequested();
                Ui(() => t.SetPhase(TaskPhase.Downloading));

                var done = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
                NativeProcess proc = svc.StartDownload(t, opts,
                    (line, _) =>
                    {
                        _log.Add(line);
                        DownloadService.ParseLine(t, line);
                    },
                    code =>
                    {
                        t.ExitCode = code;
                        done.TrySetResult(code);
                    });
                t.Proc = proc;

                using (t.Cts.Token.Register(static s => ((NativeProcess)s!).Cancel(), proc))
                {
                    uint code = await done.Task.ConfigureAwait(false);
                    string? file = t.PeekFile();
                    bool canceled = t.CancelRequested || code is 0x00010004 or 0xC000013A;
                    Ui(() =>
                    {
                        if (canceled)
                        {
                            t.SetPhase(TaskPhase.Canceled);
                        }
                        else if (code == 0)
                        {
                            t.SetPhase(TaskPhase.Done);
                            t.StatusText = !string.IsNullOrEmpty(file) ? "已完成 · " + file : "已完成";
                        }
                        else
                        {
                            string msg = t.LastError ?? $"yt-dlp 异常退出（代码 {code}）";
                            if (DownloadService.LooksLikeCookieIssue(msg))
                                msg += " —— 提示：浏览器 Cookies 解密失败，请完全退出浏览器后重试，或改用 Edge / Firefox。";
                            t.SetPhase(TaskPhase.Failed);
                            t.StatusText = msg;
                        }
                    });
                }
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            t.Proc?.Cancel();
            Ui(() =>
            {
                t.SetPhase(TaskPhase.Canceled);
                t.StatusText = "已取消";
            });
        }
        catch (Exception ex)
        {
            _log.Add("[task] " + ex.Message);
            Ui(() =>
            {
                t.SetPhase(TaskPhase.Failed);
                t.StatusText = ex.Message;
            });
        }
        finally
        {
            t.Proc?.Dispose();
            Ui(() => t.IsBusy = false);
        }
    }

    public void CancelTask(DownloadTask t)
    {
        t.CancelRequested = true;
        try { t.Cts.Cancel(); } catch { /* 已取消 */ }
        t.Proc?.Cancel();
        Ui(() =>
        {
            t.SetPhase(TaskPhase.Canceled);
            t.StatusText = "已取消";
        });
    }

    public void OpenTask(DownloadTask t)
    {
        string? path = t.FilePath;
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(OutputDir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToolStatus = "无法打开: " + ex.Message;
        }
    }

    /// <summary>UI 定时器 10Hz 调用：把全部任务的跨线程快照落到可观察属性。</summary>
    public void FlushUi()
    {
        foreach (DownloadTask t in Tasks)
            t.FlushSnapshotToUi();
    }
}
