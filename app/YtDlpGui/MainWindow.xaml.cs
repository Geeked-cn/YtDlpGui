using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using YtDlpGui.Models;
using YtDlpGui.Services;
using YtDlpGui.ViewModels;

namespace YtDlpGui;

public sealed partial class MainWindow : Window
{
    public MainViewModel Vm { get; }

    public MainWindow()
    {
        // x:Bind 在 InitializeComponent 期间就绑定 Vm，必须先建模型
        Vm = new MainViewModel(DispatcherQueue);
        InitializeComponent();

        Title = "视频下载器 · yt-dlp";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();

        WireUi();

        // 进度/日志统一 10Hz 刷新：原生线程只写快照，UI 按节拍拉取（避免高频跨线程绑定刷新）
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(100);
        timer.Tick += (_, _) => FlushUi();
        timer.Start();

        Closed += (_, _) => Vm.SaveSettings();
        _ = InitializeAsync();
    }

    private void WireUi()
    {
        QualityCombo.ItemsSource = Vm.QualityOptions;
        QualityCombo.SelectedIndex = Math.Max(0, Vm.QualityOptions.IndexOf(Vm.SelectedQuality));

        ContainerCombo.ItemsSource = Vm.ContainerOptions;
        ContainerCombo.SelectedIndex = Math.Max(0, Vm.ContainerOptions.IndexOf(Vm.SelectedContainer));

        PlaylistBox.IsChecked = Vm.WholePlaylist;
        MetaBox.IsChecked = Vm.EmbedMetadata;

        AutoDlSwitch.IsOn = Vm.AutoDownloadTools;
        MirrorCombo.SelectedIndex = Math.Clamp(Vm.MirrorIndex, 0, 2);
        ConcCombo.SelectedIndex = Math.Clamp(Vm.Concurrent, 1, 4) - 1;
    }

    private async Task InitializeAsync()
    {
        await Vm.LoadAsync();
        CookieCombo.ItemsSource = Vm.BrowserOptions;
        CookieCombo.SelectedIndex = Math.Max(0, Vm.BrowserOptions.IndexOf(Vm.SelectedBrowser));

        if (Vm.YtDlpPath is null)
        {
            if (!Vm.AutoDownloadTools)
            {
                Vm.ToolStatus = "未找到 yt-dlp.exe：请把 yt-dlp.exe 放到应用目录 tools\\ 下，或在设置中开启自动下载";
                return;
            }
            bool go = await ConfirmAsync("缺少 yt-dlp",
                "本机未找到 yt-dlp.exe。\n\n是否自动下载最新版到应用目录？（约 18MB）");
            if (!go || !await Vm.InstallYtDlpAsync())
                return;
        }

        if (Vm.FfmpegPath is null && Vm.AutoDownloadTools)
        {
            bool go = await ConfirmAsync("缺少 ffmpeg",
                "未找到 ffmpeg.exe。合成高清视频 / 提取音频需要它。\n\n是否自动下载？（约 90MB，下载后解压到应用目录）");
            if (go)
                await Vm.InstallFfmpegAsync();
        }

        Vm.CreateService();
        Vm.SaveSettings();
    }

    // ---------- 10Hz 刷新 ----------

    private void FlushUi()
    {
        Vm.FlushUi();
        if (Vm.Log.TryDrain(out string batch))
        {
            LogBox.Text += batch;
            if (LogBox.Text.Length > 300_000)
            {
                string tail = LogBox.Text[^150_000..];
                LogBox.Text = tail[(tail.IndexOf('\n') + 1)..];
            }
            LogBox.SelectionStart = LogBox.Text.Length;
            LogBox.SelectionLength = 0;
        }
    }

    // ---------- 输入区 ----------

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Vm.UrlInput))
        {
            string? clip = await GetClipboardTextAsync();
            if (!string.IsNullOrWhiteSpace(clip))
                Vm.UrlInput = clip.Trim();
        }
        await Vm.StartFromInputAsync();
    }

    private void OnUrlBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shiftDown = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == Windows.System.VirtualKey.Enter && shiftDown)
            return; // Shift+Enter 换行
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            OnDownloadClick(sender, e);
        }
    }

    private async void OnPasteClick(object sender, RoutedEventArgs e)
    {
        string? clip = await GetClipboardTextAsync();
        if (string.IsNullOrWhiteSpace(clip)) return;
        Vm.UrlInput = string.IsNullOrWhiteSpace(Vm.UrlInput) ? clip.Trim() : Vm.UrlInput.TrimEnd() + "\n" + clip.Trim();
    }

    private static async Task<string?> GetClipboardTextAsync()
    {
        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
                return await content.GetTextAsync();
        }
        catch
        {
            // 剪贴板被占用等场景
        }
        return null;
    }

    // ---------- 选项 ----------

    private void OnCookieSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CookieCombo.SelectedItem is BrowserOption b)
        {
            Vm.SelectedBrowser = b;
            // 选“不使用”时把状态一并落盘，重启后不会回退到自动检测
            if (b.Id.Length == 0)
                Vm.UseCookies = false;
            else if (!Vm.UseCookies)
                Vm.UseCookies = true;
            Vm.SaveSettings();
        }
    }

    private void OnQualitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityCombo.SelectedItem is QualityOption q)
        {
            Vm.SelectedQuality = q;
            Vm.SaveSettings();
        }
    }

    private void OnContainerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContainerCombo.SelectedItem is ContainerOption c)
        {
            Vm.SelectedContainer = c;
            Vm.SaveSettings();
        }
    }

    private void OnPlaylistChecked(object sender, RoutedEventArgs e) { Vm.WholePlaylist = true; Vm.SaveSettings(); }
    private void OnPlaylistUnchecked(object sender, RoutedEventArgs e) { Vm.WholePlaylist = false; Vm.SaveSettings(); }
    private void OnMetaChecked(object sender, RoutedEventArgs e) { Vm.EmbedMetadata = true; Vm.SaveSettings(); }
    private void OnMetaUnchecked(object sender, RoutedEventArgs e) { Vm.EmbedMetadata = false; Vm.SaveSettings(); }

    private void OnAutoDlToggled(object sender, RoutedEventArgs e) { Vm.AutoDownloadTools = AutoDlSwitch.IsOn; Vm.SaveSettings(); }

    private void OnMirrorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Vm.MirrorIndex = MirrorCombo.SelectedIndex < 0 ? 0 : MirrorCombo.SelectedIndex;
        Vm.SaveSettings();
    }

    private void OnConcSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConcCombo.SelectedIndex >= 0)
            Vm.Concurrent = ConcCombo.SelectedIndex + 1;
        Vm.SaveSettings();
    }

    // ---------- 目录 ----------

    private async void OnPickDirClick(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            Vm.OutputDir = folder.Path;
            Vm.SaveSettings();
        }
    }

    private void OnOpenDirClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.IO.Directory.Exists(Vm.OutputDir))
                _ = System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Vm.OutputDir}\"") { UseShellExecute = true });
        }
        catch { /* 打开失败静默 */ }
    }

    // ---------- 任务列表（DataTemplate 事件） ----------

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadTask t)
            Vm.CancelTask(t);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadTask t)
            Vm.OpenTask(t);
    }

    // ---------- 杂项 ----------

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = message,
            XamlRoot = Content.XamlRoot,
            PrimaryButtonText = "下载",
            CloseButtonText = "暂不",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }
}
