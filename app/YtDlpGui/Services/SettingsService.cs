using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpGui.Services;

public sealed class AppSettings
{
    public string OutputDir { get; set; } = "";
    public int Concurrent { get; set; } = 2;
    public bool AutoDownloadTools { get; set; } = true;
    public int MirrorIndex { get; set; }
    public bool EmbedMetadata { get; set; } = true;
    public bool WholePlaylist { get; set; }
    public bool UseCookies { get; set; } = true;
    public string Quality { get; set; } = "best";
    public string Container { get; set; } = "mp4";
    public string Browser { get; set; } = "";
}

public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YtDlpGui");

    private static readonly string FileName = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
    };

    /// <summary>读取资源管理器“下载”文件夹的真实位置（兼容 OneDrive 重定向）。</summary>
    public static string DefaultOutputDir
    {
        get
        {
            try
            {
                if (Microsoft.Win32.Registry.GetValue(
                        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders",
                        "{374DE290-123F-4565-9164-39C4925E467B}", null) is string v && Directory.Exists(v))
                    return v;
            }
            catch
            {
                // 注册表不可读则回退
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FileName))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FileName), Opts);
                if (s != null)
                {
                    if (string.IsNullOrWhiteSpace(s.OutputDir)) s.OutputDir = DefaultOutputDir;
                    return s;
                }
            }
        }
        catch
        {
            // 配置损坏则使用默认值
        }
        return new AppSettings { OutputDir = DefaultOutputDir };
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FileName, JsonSerializer.Serialize(s, Opts));
        }
        catch
        {
            // 保存失败不致命
        }
    }
}
