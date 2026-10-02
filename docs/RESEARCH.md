# yt-dlp 调研笔记（面向 GUI 封装）

> 调研对象：https://github.com/yt-dlp/yt-dlp （2026-10-02，本机版本 2026.08.19）

## 定位结论

yt-dlp 是 Unlicense（公有领域）的命令行下载器，官方支持被封装（wrapper）。
**不需要也不应该重写它的任何下载逻辑**，封装层只做：进程管理、参数组装、输出解析。

## 关键 CLI 能力（本项目的使用方式）

### 1. 最佳画质下载
- 默认格式选择已等价于 `-f "bv*+ba/b"`（最佳视频+最佳音频合成，需 ffmpeg）
- MP4 兼容优先：`-f "bv*[ext=mp4]+ba[ext=m4a]/bv*+ba/b" --merge-output-format mp4`
- 无损保留原始编码：`-f "bv*+ba/b" --merge-output-format mkv`
- 分辨率上限：`bv*[height<=1080]+ba/b[height<=1080]/b`
- 音质优先排序可用 `-S`（如 `-S res,fps,vcodec:avc`），本项目未启用以保"最清晰"

### 2. 浏览器 Cookies
```
--cookies-from-browser BROWSER[+KEYRING][:PROFILE][::CONTAINER]
BROWSER ∈ brave, chrome, chromium, edge, firefox, opera, safari, vivaldi, whale
```
- Windows 下自动读取浏览器本地 Cookie 数据库并解密
- Chrome 127+ 应用绑定加密（app-bound encryption）可能导致解密失败——上游已知问题，
  UI 层对含 "cookie/decrypt" 的错误给出针对性提示
- 备选：`--cookies FILE`（Netscape 格式导出文件）

### 3. 机器可读输出（封装的核心依赖）
- `-J`：整个视频（或播放列表）单行 JSON 元数据；`--playlist-items 1` 限制只取首个
- `--newline`：进度按行输出
- `--progress-template "download:TEMPLATE"`：自定义进度行，支持
  `%(progress._percent_str)s`、`%(progress._speed_str)s`、`%(progress._eta_str)s`、
  `%(progress._downloaded_bytes_str)s`、`%(progress._total_bytes_estimate_str)s`
  —— 本项目用哨兵前缀 `__PROG__|` 便于零歧义解析
- `--print "after_move:__FILE__%(filepath)s"`：取后处理完成后的最终文件路径
- 注意：`--print` 会隐式启用 quiet，需要显式 `--no-quiet` 恢复 `[Merger]` 等日志

### 4. 性能相关参数
- `-N 8`（`--concurrent-fragments`）：DASH/HLS 分片并发，对高清视频提速显著
- `--ffmpeg-location PATH`：显式指定 ffmpeg（项目内置 ffmpeg 时使用）
- 外部下载器 aria2c 可选（本项目未用，避免额外依赖）
- `--impersonate chrome` 需 curl_cffi（官方 exe 已内置）可绕过部分站点指纹检测

### 5. 2025+ 新变化（重要）
- **yt-dlp-ejs + JS 运行时**：完整 YouTube 支持需要 JS 解释器，推荐 deno。
  官方 `yt-dlp.exe` 已随版本处理；若从 pip 安装需注意 `yt-dlp[default]`
- 版本 >90 天会打印更新警告；`--no-update` 可关闭

### 6. 其他
- 退出码官方未承诺稳定语义，封装层应以 `ERROR:` 行 + 退出码 != 0 综合判断
- `-o` 默认模板即 `%(title)s [%(id)s].%(ext)s`；`--windows-filenames` 处理非法字符
- `--ignore-config` 隔离用户全局配置，保证 GUI 行为确定
- `-x --audio-format mp3 --audio-quality 0`：音频提取

## 构建封装层时发现的坑（本项目已解决）

1. **PyInstaller 双进程**：yt-dlp.exe 是 PyInstaller onefile（引导进程 + Python 子进程），
   进程树必须用 Job Object 整体管理，否则取消下载会留孤儿进程
2. **argv[0] 必须是程序名**：`CreateProcessW` 的 lpApplicationName 与 lpCommandLine 分离，
   命令行首 token 若不是程序路径，子进程 CRT 解析出的 argv 会整体错位
   （实测：ffmpeg 把 `-version` 当成了程序名，PyInstaller 直接挂起）
3. **Python 管道编码**：Windows 下 Python 写管道默认用本地代码页（中文系统为 GBK），
   必须在子进程环境注入 `PYTHONUTF8=1` 才能得到 UTF-8 输出
4. **`--print` 隐式 quiet**：见上，需 `--no-quiet`

## WinUI3 纯 dotnet CLI 构建的坑（本项目已解决）

1. `x:Bind 函数绑定`（如 `{x:Bind ToVisibility(IsBusy), Mode=OneWay}`）会让
   XamlCompiler **静默**退出（exit 1，无任何错误输出）——改用类型化属性绑定
2. PriGen（resources.pri 生成）官方依赖 VS 的 AppxPackage MSBuild 任务程序集
   （`Microsoft.Build.AppxPackage.dll`、`Microsoft.Build.Packaging.Pri.Tasks.dll`），
   纯 dotnet SDK 环境没有 —— 两个选择：
   - 安装 VS Build Tools（AppxPackaging 组件）后用 VS MSBuild 构建
   - 本项目的方案：`AppxGeneratePriEnabled=false` 关闭托管管线，
     用 Windows SDK 自带的 `makepri.exe` 在 AfterTargets=Build 里把 XBF 打包进
     resources.pri（见 YtDlpGui.csproj 的 GenerateResourcesPri target）
3. 打包外（unpackaged）自包含部署三件套：
   `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` +
   `WindowsAppSdkUndockedRegFreeWinRTInitialize=true`
