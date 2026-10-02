using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using YtDlpGui.Services;

namespace YtDlpGui;

// DISABLE_XAML_GENERATED_MAIN：自定义入口，便于打包外 (unpackaged) 部署
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BootLog.Write("main: enter");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        BootLog.Write("main: comwrappers ok");
        Application.Start(p =>
        {
            // 关键：安装 DispatcherQueue 同步上下文，
            // 否则 WinRT 异步（剪贴板/选择器等）的 await 续体会落到线程池，
            // 之后任何 UI 可观察属性更新都会抛 RPC_E_WRONG_THREAD 直接崩溃
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            BootLog.Write("Application.Start callback enter");
            _ = new App();
            BootLog.Write("Application.Start callback done");
        });
        BootLog.Write("main: Application.Start returned");
    }
}
