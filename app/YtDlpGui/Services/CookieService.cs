using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YtDlpGui.Services;

/// <summary>可传给 yt-dlp --cookies-from-browser 的浏览器选项。</summary>
public sealed record BrowserOption(string Id, string DisplayName)
{
    public static readonly BrowserOption None = new("", "不使用 Cookies");
    public override string ToString() => DisplayName;
}

/// <summary>扫描本机已安装的浏览器（yt-dlp 原生支持从其数据库自动读取 Cookies）。</summary>
public static class CookieService
{
    public static List<BrowserOption> Detect()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var list = new List<BrowserOption>();

        void Add(string id, string name, params string?[] dirs)
        {
            if (dirs.Any(d => !string.IsNullOrEmpty(d) && Directory.Exists(d!)))
                list.Add(new BrowserOption(id, name));
        }

        // 顺序即"自动"回退顺序：Edge 随系统自带最稳妥，其次 Chrome、Firefox
        Add("edge", "Microsoft Edge",
            Path.Combine(local, @"Microsoft\Edge\User Data"));
        Add("chrome", "Google Chrome",
            Path.Combine(local, @"Google\Chrome\User Data"));
        Add("firefox", "Firefox",
            Path.Combine(roaming, @"Mozilla\Firefox\Profiles"));
        Add("brave", "Brave",
            Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"));
        Add("vivaldi", "Vivaldi",
            Path.Combine(local, @"Vivaldi\User Data"));
        Add("opera", "Opera",
            Path.Combine(roaming, @"Opera Software"));
        Add("whale", "Whale",
            Path.Combine(local, @"Naver\Whale\User Data"));
        Add("chromium", "Chromium",
            Path.Combine(local, @"Chromium\User Data"));
        return list;
    }
}
