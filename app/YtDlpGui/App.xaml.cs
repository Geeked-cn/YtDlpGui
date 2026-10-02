using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using YtDlpGui.Services;

namespace YtDlpGui;

public partial class App : Application
{
    public static Window? MainAppWindow;

    public App()
    {
        BootLog.Write("App.ctor enter");
        InitializeComponent();
        BootLog.Write("App.ctor InitializeComponent ok");
        this.UnhandledException += (_, e) =>
        {
            BootLog.Write("Application.UnhandledException", e.Exception);
            e.Handled = false;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        BootLog.Write("OnLaunched enter");
        try
        {
            MainAppWindow = new MainWindow();
            BootLog.Write("OnLaunched MainWindow created");
            MainAppWindow.Activate();
            BootLog.Write("OnLaunched Activated");
        }
        catch (Exception ex)
        {
            // 打包外应用没有统一错误界面，启动失败时用系统消息框把原因亮出来
            BootLog.Write("OnLaunched failed", ex);
            MessageBoxW(IntPtr.Zero, ex.ToString(), "视频下载器启动失败", 0x10 /* MB_ICONERROR */);
            throw;
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
