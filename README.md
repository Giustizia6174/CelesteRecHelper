# CelesteRecHelper

按 [PLAN.md](PLAN.md) 开发的 Everest 录制助手。当前完成阶段 1–2：模组骨架、设置、Core 模型、原子持久化/恢复、OBS 连接与测试回放，以及 Worker 媒体探测。游戏事件驱动的录制、长片段交接和最终剪辑按后续阶段接入。

## 构建与部署

需要 Windows PowerShell/PowerShell、.NET 8 SDK 或更高版本，以及安装了 Everest 的 Celeste。项目使用官方 `CelesteMod.Templates` 1.0.24 建立，再按计划整理为多项目结构。游戏引用直接使用本机 `Celeste.dll`、`MMHOOK_Celeste.dll`、`FNA.dll`，不会打包游戏程序集。

```powershell
.\build.ps1
# 非 Steam 安装或未能自动发现时：
.\build.ps1 -CelesteDirectory 'D:\Games\Celeste'
# 构建、测试和生成目录，暂不复制进游戏：
.\build.ps1 -SkipDeploy
```

脚本优先使用用户目录 `.dotnet/dotnet.exe`，否则使用 PATH 中的 dotnet。自动从 Steam 库定位游戏，构建四个项目并运行测试，发布 Worker，生成 `dist/<配置>/CelesteRecHelper/`，最后复制到 `<游戏>/Mods/CelesteRecHelper/`。首次安装会完整暂存后移入 Mods，Everest 可自动加载；游戏正在运行时拒绝覆盖已经安装的模组。Worker 当前使用依赖框架的发布方式，自包含发行包按计划在阶段 6 完成。

IDE/直接构建时设置 `CELESTE_DIR` 或传入 `-p:CelesteDirectory=...`。Core 和 Worker 不依赖游戏，可分别构建；测试可直接运行：

```powershell
dotnet test tests/CelesteRecHelper.Core.Tests/CelesteRecHelper.Core.Tests.csproj
```

本机验证基线：Celeste 1.4.0.0、Everest 1.6662.0 stable、.NET 8。`everest.yaml` 的 EverestCore 最低依赖暂设为此已验证版本。

## 模组设置

游戏的 Mod 选项中提供启用开关、结束键（默认 F8）、撤销键（默认 F9）、OBS 主机/端口/密码和视频输出目录。开关默认关闭。当前按键和开关仍是设置入口；游戏事件驱动的录制在阶段 4 接入。阶段 2 可通过“保存测试回放”独立验证采集链路。

OBS 默认主机 `127.0.0.1`、端口 `4455`。输出目录留空表示使用 OBS 录像目录。密码在选项列表里隐藏，修改时使用 Everest 的标准文本编辑器。设置由 Everest 持久化；密码不写入路线清单和日志。

## 数据与恢复

固定根目录是 Everest 的存档目录下的 `CelesteRecHelper/`；普通 Windows 安装即 `<游戏>/Saves/CelesteRecHelper/`，不受视频输出目录影响。

```text
CelesteRecHelper/
  routes/<路线 GUID>/manifest.json
  jobs/<任务 GUID>/status.json
  obs-directory-recovery.json
```

每次提交清单先序列化到同目录的独立临时文件，刷新到磁盘后再替换正式文件。正式清单保留 schema 版本，保存原始 Stopwatch ticks、频率和时钟会话信息。只保存当前这条命，死亡历史不累积。文件不会由阶段 1 自动删除。

启动时把录制中的路线标为已中断，清除无法续接的当前生命；已保存片段保留。未完成保存只在预期目录中找到唯一时间匹配、非空、且此前不存在/未被其他素材占用的文件时恢复。同一文件匹配多个请求、多个候选或没有文件时，保留文件并标记保存失败。按时间推断恢复的素材有专门标记，后续媒体探测仍需检查覆盖范围；缺少真实保存事件的时间戳不会被伪造。损坏或未知版本的清单保留原状并记录警告，不影响其他路线恢复。

OBS 目录恢复记录分别保留简单/高级输出原目录和配置档，不含密码。执行游戏内 OBS 连接检查时，会核对端点、配置档和输出状态，再分别恢复两个原目录；配置不符、输出仍活动或发生未知错误时保留恢复记录并提示。未成功完成的后台任务列入启动恢复报告，Worker 执行和重试在后续阶段接入。

Worker 骨架支持只读列出路线：

```powershell
.\dist\Debug\CelesteRecHelper\worker\CelesteRecHelper.Worker.exe routes 'D:\Games\Celeste\Saves\CelesteRecHelper'
```

阶段 1 测试覆盖清单跨实例读写、写入失败不破坏旧清单、临时文件隔离、启动恢复、保存匹配歧义、OBS 恢复记录和未完成任务。

本机验证（2026-11-18）：使用 .NET SDK 8.0.428 构建，0 警告、0 错误；23 项测试全部通过。首次部署后，Everest 1.6662.0 的运行日志确认主模组及 Core 程序集加载成功，持久化/启动恢复初始化完成，DebugRC 的模组列表包含 `CelesteRecHelper`。Worker 的帮助和只读路线列表命令已运行通过。

## 阶段 2：OBS 连接与媒体探测

在 OBS 的“工具 → WebSocket 服务器设置”中启用服务器；默认端口 4455。将主机、端口和密码填入模组设置。密码只用于鉴权，不写入诊断文件。RPC 1 客户端订阅配置/输出事件，断线后自动重连；录制和保存命令不会自动重发。

模组菜单提供“检查 OBS 连接”和“保存测试回放（约 3 秒）”。连接检查显示连接、缓存可用性、估算缓存时长和验证状态，并提示缓存过短或输出已被占用。测试回放会独占启动缓存、等待约 3 秒、保存并等待完成事件、停止缓存，随后启动隐藏的 Worker 探测视频。用户已有普通录制或缓存时拒绝启动。测试素材保留，诊断结果写入固定数据根目录的 `diagnostics/last-replay.json`。

缓存估算优先读取实际输出的 `max_time_sec` / `max_size_mb`，输出尚未配置时回退到当前输出模式的 profile 参数。内存限制为零时只按时长估算；质量编码先按 50 Mbps 保守估计，保存探测后用文件大小/时长得到实际码率，再加 50% 余量修正。未验证配置的交接阈值最多 30 秒。这里的验证状态针对缓存估算，已实测的编码组合见 PLAN 第四节。

保存以 `ReplayBufferSaved` 事件为准，检查新文件是否位于预期目录并已写入数据。超时/断线后禁止在同一次缓存会话继续保存，以免迟到事件被错配到下一次请求；停止并重新启动后才能继续。用户在请求窗口内手动保存回放仍可能干扰对应关系，应避免同时使用 OBS 保存热键。

Worker 新增以下命令：

```powershell
# 只读检查当前 OBS；密码可预先放入 CELESTE_REC_OBS_PASSWORD 环境变量
.\dist\Debug\CelesteRecHelper\worker\CelesteRecHelper.Worker.exe obs-check

# 探测视频，包含容器时长、实际首末帧 PTS、全部音轨及原始 ffprobe 元数据
.\dist\Debug\CelesteRecHelper\worker\CelesteRecHelper.Worker.exe probe 'D:\Videos\回放.mkv' --output '.\dist\probe.json'

# 明确发起一次采集测试；先确保 OBS 普通录制和回放缓存均未运行
.\dist\Debug\CelesteRecHelper\worker\CelesteRecHelper.Worker.exe obs-save --wait-seconds 4 --data-dir 'D:\Games\Celeste\Saves\CelesteRecHelper' --output '.\dist\obs-test.json'
```

`obs-save` 必须显式指定固定数据目录 `--data-dir`，以便游戏能在崩溃后找到 OBS 恢复记录；还支持 `--endpoint`、`--password-env`、`--output-directory` 和 `--ffprobe`；使用自定义目录前会持久化两种输出模式的原目录，停止后恢复。探测视频时逐行读取帧时间戳，避免将长视频的所有帧信息加载到内存。外部进程使用独立参数列表，中文/空格路径受测试覆盖。

ffprobe 查找与本地工具来源见 [tools/README.md](tools/README.md)。构建脚本发现 `tools/ffprobe.exe` 时会随 Worker 放入成品目录；缺少时可用 `CELESTE_REC_FFPROBE` 或 PATH。媒体集成测试需要 ffmpeg/ffprobe；缺少工具时明确标为跳过。

阶段 2 验证（2026-11-18）：构建 0 警告、0 错误；41 项测试全部通过、无跳过。独立 OBS 32.2.2 / obs-websocket 5.7.4 实测简单输出 1 音轨、高级输出 2 音轨，均完成保存事件 → 新文件 → Worker 探测；同时录制时日志确认共用视频编码器。测试未修改用户现有 OBS profile。

阶段 2 新版已部署至本机游戏，实际启动日志确认主模组/Core 加载和恢复初始化成功，游戏完成启动。

2026-11-19 连接兼容性修正：OBS 在回放缓存未启用或不可用时，`GetReplayBufferStatus` 可能返回 604（InvalidResourceState）。连接检查现将 504/604 显示为回放不可用，保留正常连接状态；其他错误仍向上报告。已补充连接诊断及目录恢复回归测试，44 项测试通过，并通过真实 OBS 的密码鉴权验证。
