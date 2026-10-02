# 视频下载器 · yt-dlp GUI

一个基于 **WinUI 3 (C#)** 的 yt-dlp 图形前端：粘贴任意视频网站的链接即可下载最清晰的画质，
自动检测并使用浏览器 Cookies，内置 yt-dlp / ffmpeg 自动下载。**不重写 yt-dlp，直接引用官方
`yt-dlp.exe`**，由自研的 C 原生层（`ytdlp_host.dll`）负责进程管理。

![架构](docs/architecture.md)

```
┌─────────────────────────────┐
│  WinUI 3 前端 (C# / .NET 8)  │  界面、任务队列、设置、剪贴板
└──────────────┬──────────────┘
               │ P/Invoke (cdecl)
┌──────────────▼──────────────┐
│  ytdlp_host.dll (纯 C)       │  进程启动/终止、UTF-8 管道读、Job Object
└──────────────┬──────────────┘
               │ 直接调用（不重写）
┌──────────────▼──────────────┐
│  yt-dlp.exe  +  ffmpeg.exe  │  官方原版二进制
└─────────────────────────────┘
```

## 功能

- **任意站点**：支持 yt-dlp 全部站点（数千个），包括 YouTube、B站、推特等
- **最佳画质**：默认 `-f bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b` 合成最高码率视频+音频
- **自动 Cookies**：自动检测 Edge / Chrome / Firefox / Brave / Vivaldi / Opera，
  通过 yt-dlp 原生 `--cookies-from-browser` 读取登录态（下载会员/年龄限制内容）
- **批量下载**：多行链接一次导入；自动识别播放列表（可选只下单个）
- **实时进度**：百分比、速度、剩余时间、已下载大小；可随时取消（整棵进程树终止）
- **组件自维护**：检测不到 yt-dlp / ffmpeg 时一键自动下载（支持 ghproxy 镜像加速）
- **音视频选项**：MP4 兼容 / MKV 无损两种容器，最高画质 / ≤1080P / ≤720P / 仅音频(MP3)

## 构建

依赖：Windows 10 1809+、.NET 8 SDK、MSYS2 gcc（或 VS Build Tools）、Windows SDK（makepri）。

```bat
:: 1. 编译 C 原生层
cd native && build.bat && cd ..

:: 2. 编译应用
dotnet build app\YtDlpGui\YtDlpGui.csproj -c Release
```

输出：`app\YtDlpGui\bin\Release\net8.0-windows10.0.19041.0\win-x64\YtDlpGui.exe`
（打包外自包含部署：无需安装 Windows App SDK 运行时，双击即用）。

### 捆绑 yt-dlp / ffmpeg（可选）

把 `yt-dlp.exe`、`ffmpeg.exe`、`ffprobe.exe` 放进 `redist\` 目录（见 `redist\README.txt`），
构建时会自动拷贝到应用 `tools\` 下随软件分发，用户无需再下载。
缺失时应用启动会引导用户自动下载（支持 ghproxy 镜像）。

### 打包安装程序

需要 [Inno Setup 7](https://jrsoftware.org/isinfo.php)（含简体中文语言包）：

```bat
dotnet build app\YtDlpGui\YtDlpGui.csproj -c Release
"C:\Program Files\Inno Setup 7\ISCC.exe" installer\setup.iss
```

产物：`installer\Output\YtDlpGui-1.0.0-setup.exe`（按用户安装到
`%LOCALAPPDATA%\Programs\YtDlpGui`，免管理员，含开始菜单快捷方式与卸载器）。

## 目录结构

```
YtDlpGui/
├── native/                    C 原生层
│   ├── ytdlp_host.h/.c        进程宿主：spawn/管道/Job/引用计数
│   ├── test_host.c            纯 C 冒烟测试
│   └── build.bat              gcc / MSVC 双后端构建脚本
├── redist/                    捆绑的 yt-dlp/ffmpeg（*.exe 不进 git，见内 README）
├── installer/                 Inno Setup 安装程序脚本 + 图标
├── app/YtDlpGui/              WinUI 3 应用
│   ├── MainWindow.xaml(.cs)   界面
│   ├── ViewModels/            MainViewModel（任务调度/并发闸门）
│   ├── Models/                DownloadTask（跨线程快照 → 10Hz UI 刷新）
│   └── Services/
│       ├── NativeProcess.cs   P/Invoke 封装
│       ├── DownloadService.cs yt-dlp 参数组装 + 进度行解析
│       ├── CookieService.cs   浏览器检测
│       ├── ToolLocator.cs     yt-dlp/ffmpeg 定位与下载
│       └── SettingsService.cs 配置持久化
└── tests/NativeSmoke/         原生层 + P/Invoke 冒烟测试
```

## 性能优化清单

**原生 C 层（ytdlp_host.dll）**

| 优化 | 说明 |
|---|---|
| 零轮询事件驱动 | 每个管道一个阻塞读线程，`ReadFile` 天然挂起等待，无定时器无 CPU 空转 |
| 1 MB 管道缓冲 | 高速下载时 yt-dlp 进度行风暴不会反压子进程 |
| 动态行缓冲 | 长行（如 `-J` 的多 MB JSON）自动扩容，不截断 |
| UTF-8 全链路 | 原生层零转码直接透传，托管层一次性解码；子进程强制 `PYTHONUTF8=1` |
| Job Object | `CREATE_SUSPENDED` → 入 Job → `ResumeThread`，杜绝子进程逃逸窗口 |
| 引用计数生命周期 | 读线程/监视线程/托管方三方计数，无 use-after-free、无句柄泄漏 |
| argv 数组传参 | 按 Windows 标准规则加引号，无 shell 注入面、无字符串拼接开销 |

**调用层（DownloadService）**

- `-N 8`：DASH/HLS 分片并发下载，高清视频速度提升数倍
- `--newline --progress-template`：机器可读进度（哨兵前缀），不做正则猜测
- `--ignore-config`：不受用户全局配置干扰
- 单次 `-J` 元数据探测（`--playlist-items 1` 防止播放列表全量拉取），失败自动降级直下

**UI 层（C# / WinUI 3）**

- 原生线程只写**快照字段**，UI 定时器 **10 Hz 批量刷新**，高频进度不击穿 UI 线程
- 日志**环形缓冲**（4000 行上限）+ 批量取走，长时间运行内存恒定
- 确定性并发闸门（无轮询 `Gate`）：限制同时运行的 yt-dlp 进程数，支持运行中调档
- `HttpClient` 复用 + 流式下载进度回调

## 已知限制

- Chrome 127+ 的应用绑定加密可能导致 `--cookies-from-browser chrome` 解密失败
  （yt-dlp 上游限制），完全退出 Chrome 后重试，或改用 Edge / Firefox
- 构建需要 Windows SDK 的 `makepri.exe`（项目已内置 makepri 替代管线，
  不依赖 VS 的 AppxPackage MSBuild 任务，纯 `dotnet build` 可构建）

## 许可证

- 本项目代码：MIT
- yt-dlp：[Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE)（公有领域）
- ffmpeg：LGPL/GPL（取决于构建，gyan.dev full build 为 GPL）
- 使用本工具下载内容时请遵守目标网站的服务条款与当地法律
