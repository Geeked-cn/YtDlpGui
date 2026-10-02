此目录存放随安装程序捆绑的组件（体积原因不进 git 仓库）：

- yt-dlp.exe   —— https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe
- ffmpeg.exe   —— https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-full.7z 解出（或 essentials）
- ffprobe.exe  —— 同上

构建应用 / 安装程序前把这三个 exe 放到本目录；csproj 会自动把它们拷贝到
输出目录的 tools\ 下。缺失时应用仍会在启动时引导用户自动下载。
