using System;
using System.Collections.Generic;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using YtDlpGui.Services;

namespace YtDlpGui.Models;

public enum TaskPhase
{
    Queued,
    Probing,
    Downloading,
    Done,
    Failed,
    Canceled,
}

/// <summary>
/// 一个下载任务。
/// 原生线程只写“快照字段”（无 PropertyChanged），UI 定时器以 10Hz 把快照落到可观察属性，
/// 避免高频进度刷新击穿 UI 线程（性能关键点）。
/// </summary>
public partial class DownloadTask : ObservableObject
{
    public DownloadTask(string url)
    {
        Url = url;
        _title = url;
        _statusText = "排队中…";
        _phaseGlyph = "\uE823";
    }

    public string Url { get; }
    public TaskPhase Phase { get; private set; } = TaskPhase.Queued;

    public NativeProcess? Proc;
    public CancellationTokenSource Cts { get; } = new();
    public volatile bool CancelRequested;
    public uint ExitCode;

    [ObservableProperty] private string _title;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _phaseGlyph;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _isBusy = true;
    [ObservableProperty] private string? _filePath;

    /// <summary>绑定专用：IsBusy 的 Visibility 投影（x:Bind 函数绑定会卡死 XamlCompiler，勿用）。</summary>
    [ObservableProperty] private Visibility _cancelButtonVisibility = Visibility.Visible;

    partial void OnIsBusyChanged(bool value)
        => CancelButtonVisibility = value ? Visibility.Visible : Visibility.Collapsed;

    // ---- 跨线程快照（原生线程写入，UI 定时器读取） ----
    private readonly object _sync = new();
    private double _sPct = -1;
    private string _sSpeed = "";
    private string _sEta = "";
    private string _sSize = "";
    private string _sTotal = "";
    private string _sInfo = "";
    private string? _sFile;
    private readonly List<string> _errors = new();

    public void SetProgress(double pct, string speed, string eta, string size, string total)
    {
        lock (_sync)
        {
            _sPct = pct;
            _sSpeed = speed;
            _sEta = eta;
            _sSize = size;
            _sTotal = total;
        }
    }

    public void SetInfo(string info)
    {
        lock (_sync) _sInfo = info;
    }

    public void SetFile(string f) => Volatile.Write(ref _sFile, f);

    public string? PeekFile() => Volatile.Read(ref _sFile);

    public void AddError(string e)
    {
        lock (_sync) _errors.Add(e);
    }

    public string? LastError
    {
        get
        {
            lock (_sync)
                return _errors.Count > 0 ? _errors[^1] : null;
        }
    }

    /// <summary>仅在 UI 线程调用。</summary>
    public void SetPhase(TaskPhase phase)
    {
        Phase = phase;
        PhaseGlyph = phase switch
        {
            TaskPhase.Queued => "\uE823",
            TaskPhase.Probing => "\uE721",
            TaskPhase.Downloading => "\uE896",
            TaskPhase.Done => "\uE73E",
            TaskPhase.Failed => "\uE783",
            TaskPhase.Canceled => "\uE711",
            _ => "\uE896",
        };
        IsIndeterminate = phase == TaskPhase.Probing;
        IsBusy = phase is TaskPhase.Queued or TaskPhase.Probing or TaskPhase.Downloading;
        switch (phase)
        {
            case TaskPhase.Done:
                ProgressPercent = 100;
                if (!StatusText.StartsWith("已完成", StringComparison.Ordinal))
                    StatusText = "已完成";
                break;
            case TaskPhase.Canceled:
                StatusText = "已取消";
                break;
        }
    }

    /// <summary>由 UI 定时器 10Hz 调用（仅 UI 线程）。</summary>
    public void FlushSnapshotToUi()
    {
        double pct;
        string speed, eta, size, total, info;
        string? err;
        lock (_sync)
        {
            pct = _sPct;
            speed = _sSpeed;
            eta = _sEta;
            size = _sSize;
            total = _sTotal;
            info = _sInfo;
            err = _errors.Count > 0 ? _errors[^1] : null;
        }
        string? file = Volatile.Read(ref _sFile);

        if (file != null && FilePath != file)
            FilePath = file;
        if (pct >= 0 && Math.Abs(ProgressPercent - pct) > 0.05)
            ProgressPercent = pct;

        if (Phase == TaskPhase.Downloading)
        {
            var parts = new List<string>(5);
            if (info.Length > 0) parts.Add(info);
            parts.Add($"下载中 {Math.Min(pct, 100):0.#}%");
            if (speed.Length > 0 && !speed.StartsWith("Unknown", StringComparison.Ordinal)) parts.Add(speed);
            if (eta.Length > 0 && !eta.StartsWith("Unknown", StringComparison.Ordinal)) parts.Add("剩余 " + eta);
            if (size.Length > 0 && !size.StartsWith("Unknown", StringComparison.Ordinal))
                parts.Add(size + (IsValid(total) ? " / " + total : ""));
            string s = string.Join(" · ", parts);
            if (StatusText != s)
                StatusText = s;
        }
        else if (err != null && Phase is TaskPhase.Queued or TaskPhase.Probing && StatusText.Length > 0)
        {
            // 探测期错误留待任务收尾统一呈现
        }
    }

    private static bool IsValid(string s)
        => s.Length > 0 && !s.StartsWith("Unknown", StringComparison.Ordinal);
}
