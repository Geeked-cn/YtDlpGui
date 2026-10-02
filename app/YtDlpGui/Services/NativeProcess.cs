using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace YtDlpGui.Services;

/// <summary>
/// 原生 ytdlp_host.dll 的 P/Invoke 封装。
/// 生命周期约定：Exited 事件是原生层发出的最后一个回调（晚于全部行回调）；
/// 事件处理器内部持有 GC 句柄直到该事件触发，保证原生线程不会调用到已回收的委托。
/// </summary>
public sealed class NativeProcess : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LineCallback(IntPtr user, IntPtr data, uint len, int isStderr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ExitCallback(IntPtr user, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpawnInfo
    {
        public IntPtr ExePath;
        public IntPtr Argv;
        public int Argc;
        public IntPtr WorkingDir;
        public IntPtr OnLine;
        public IntPtr OnExit;
        public IntPtr User;
    }

    private const string Lib = "ytdlp_host";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ytdlp_host_version")]
    private static extern IntPtr _version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ytdlp_host_spawn", SetLastError = true)]
    private static extern int _spawn(ref SpawnInfo info, out IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ytdlp_host_cancel")]
    private static extern int _cancel(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ytdlp_host_release")]
    private static extern int _release(IntPtr handle);

    public static string NativeVersion => Marshal.PtrToStringAnsi(_version()) ?? "?";

    private readonly LineCallback _lineCb;
    private readonly ExitCallback _exitCb;
    private readonly GCHandle _self;          // 强 GC 句柄：原生回调期间对象不会被回收
    private IntPtr _handle;

    public event Action<string, bool>? Line;  // (行文本, 是否 stderr)
    public event Action<uint>? Exited;

    private NativeProcess()
    {
        _lineCb = OnLineNative;
        _exitCb = OnExitNative;
        _self = GCHandle.Alloc(this);
    }

    public static NativeProcess Start(string exePath, IReadOnlyList<string> args,
        Action<string, bool>? onLine = null, Action<uint>? onExit = null, string? workingDir = null)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException("找不到可执行文件: " + exePath, exePath);

        var p = new NativeProcess();
        if (onLine != null) p.Line += onLine;
        if (onExit != null) p.Exited += onExit;

        IntPtr exePtr = Marshal.StringToCoTaskMemUTF8(exePath);
        IntPtr cwdPtr = workingDir is null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(workingDir);
        // 原生层约定 argv[0] = 程序名，其后为参数
        IntPtr argvBuf = Marshal.AllocHGlobal(IntPtr.Size * (args.Count + 2));
        var tempArgs = new List<IntPtr>(args.Count);
        try
        {
            Marshal.WriteIntPtr(argvBuf, 0, exePtr);
            for (int i = 0; i < args.Count; i++)
            {
                IntPtr s = Marshal.StringToCoTaskMemUTF8(args[i] ?? string.Empty);
                tempArgs.Add(s);
                Marshal.WriteIntPtr(argvBuf, IntPtr.Size * (i + 1), s);
            }
            Marshal.WriteIntPtr(argvBuf, IntPtr.Size * (args.Count + 1), IntPtr.Zero);

            var info = new SpawnInfo
            {
                ExePath = exePtr,
                Argv = argvBuf,
                Argc = args.Count + 1,
                WorkingDir = cwdPtr,
                OnLine = Marshal.GetFunctionPointerForDelegate(p._lineCb),
                OnExit = Marshal.GetFunctionPointerForDelegate(p._exitCb),
                User = GCHandle.ToIntPtr(p._self),
            };
            int rc = _spawn(ref info, out IntPtr h);
            if (rc != 0)
                throw new Win32Exception(rc & 0xFFFF, $"ytdlp_host_spawn 失败 (rc={rc})");
            p._handle = h;
            return p;
        }
        finally
        {
            // spawn 内部已同步完成命令行/环境的拷贝，这里可以立即归还临时内存
            Marshal.FreeCoTaskMem(exePtr);
            if (cwdPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(cwdPtr);
            foreach (var s in tempArgs) Marshal.FreeCoTaskMem(s);
            Marshal.FreeHGlobal(argvBuf);
        }
    }

    private void OnLineNative(IntPtr user, IntPtr data, uint len, int isStderr)
    {
        try
        {
            string s = len == 0 ? string.Empty
                : Marshal.PtrToStringUTF8(data, checked((int)len)) ?? string.Empty;
            Line?.Invoke(s, isStderr != 0);
        }
        catch
        {
            // 异常绝不允许穿越原生回调边界
        }
    }

    private void OnExitNative(IntPtr user, uint exitCode)
    {
        try { Exited?.Invoke(exitCode); }
        catch { /* 同上 */ }
        if (_self.IsAllocated) _self.Free(); // 之后原生不会再有任何回调
    }

    public bool IsRunning => _handle != IntPtr.Zero;

    /// <summary>终止整棵进程树（含 ffmpeg 等子进程）。幂等。</summary>
    public bool Cancel() => _handle != IntPtr.Zero && _cancel(_handle) == 0;

    public void Dispose()
    {
        IntPtr h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero)
            _release(h);
    }

    ~NativeProcess()
    {
        IntPtr h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero)
            _release(h);
    }
}
