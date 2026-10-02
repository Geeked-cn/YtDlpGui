using System;
using System.Collections.Concurrent;
using System.Text;

namespace YtDlpGui.Services;

/// <summary>线程安全日志环形缓冲：任意线程写入，UI 定时器批量取走。</summary>
public sealed class Logger
{
    private readonly ConcurrentQueue<string> _lines = new();
    private int _count;

    public void Add(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _lines.Enqueue(line);
        if (Interlocked.Increment(ref _count) > 4000 && _lines.TryDequeue(out _))
            Interlocked.Decrement(ref _count);
    }

    public bool TryDrain(out string batch)
    {
        if (_lines.IsEmpty)
        {
            batch = "";
            return false;
        }
        var sb = new StringBuilder();
        while (_lines.TryDequeue(out var l))
        {
            sb.AppendLine(l);
            Interlocked.Decrement(ref _count);
        }
        batch = sb.ToString();
        return true;
    }
}
