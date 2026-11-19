# 本地媒体工具

阶段 2 的 Worker 使用 `ffprobe` 探测媒体；阶段 3 另用 `ffmpeg` 解码并匹配交接重叠画面。将 `ffprobe.exe` 和 `ffmpeg.exe` 放入本目录，构建脚本会复制到成品目录的 `tools/`。Worker 分别支持 `--ffprobe <路径>` / `CELESTE_REC_FFPROBE`、`--ffmpeg <路径>` / `CELESTE_REC_FFMPEG`，以及 PATH。

媒体探测与交接验证的集成测试都需要这两项工具。缺少任一工具时，相关测试会明确标为跳过，其余测试不受影响；正式运行交接验证时缺少工具则报错，不会跳过验证。二进制文件由 `.gitignore` 排除。

本机测试工具：FFmpeg 9.0.2，GyanD essentials build。
- 官方下载入口：https://ffmpeg.org/download.html
- 构建来源：https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2
- 本地 ffmpeg / ffprobe 二进制配套许可：`ffprobe-LICENSE.txt`（构建方提供的 GPLv3 文本）。

这不是完整发行包；阶段 6 打包时仍需按实际构建配置补齐对应源码获取信息和许可材料。
