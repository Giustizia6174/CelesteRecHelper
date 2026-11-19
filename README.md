# CelesteRecHelper

按 [PLAN.md](PLAN.md) 开发的 Everest 录制助手。当前完成阶段 1–3：模组骨架、设置、Core 模型、原子持久化/恢复、OBS 连接与测试回放、Worker 媒体探测，以及手动长片段交接与画面重叠验证。游戏事件驱动的录制、自动交接和最终剪辑按后续阶段接入。阶段 4 已完成 Core 片段状态机，当前处于第一部分核查点。

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

游戏的 Mod 选项中提供启用开关、结束键（默认 F8）、撤销键（默认 F9）、OBS 主机/端口/密码和视频输出目录。开关默认关闭。当前按键和开关仍是设置入口；游戏事件驱动的录制在阶段 4 接入。可通过“保存测试回放”独立验证采集链路，通过“测试长片段交接”验证缓存与普通录制的覆盖关系。

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

缓存估算优先读取实际输出的 `max_time_sec` / `max_size_mb`，输出尚未配置时回退到当前输出模式的 profile 参数。内存限制为零时只按时长估算；质量编码先按 50 Mbps 保守估计，保存探测后用文件大小/时长得到实际码率，再加 50% 余量修正。阶段 3 将安全余量固定为 `max(30% × C, 15 秒)`，交接阈值为 `max(0, C − 安全余量)`；未验证配置另将阈值限制在最多 30 秒。这里的验证状态针对缓存估算，已实测的编码组合见 PLAN 第四节。

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

媒体工具的查找与本地来源见 [tools/README.md](tools/README.md)。构建脚本会将本地 `tools/ffprobe.exe` 和 `tools/ffmpeg.exe` 随 Worker 放入成品目录：ffprobe 用于探测，ffmpeg 用于交接帧匹配。缺少时可分别使用 `CELESTE_REC_FFPROBE`、`CELESTE_REC_FFMPEG` 或 PATH。媒体集成测试需要这两项工具；缺少工具时明确标为跳过。

阶段 2 验证（2026-11-18）：构建 0 警告、0 错误；41 项测试全部通过、无跳过。独立 OBS 32.2.2 / obs-websocket 5.7.4 实测简单输出 1 音轨、高级输出 2 音轨，均完成保存事件 → 新文件 → Worker 探测；同时录制时日志确认共用视频编码器。测试未修改用户现有 OBS profile。

阶段 2 新版已部署至本机游戏，实际启动日志确认主模组/Core 加载和恢复初始化成功，游戏完成启动。

2026-11-19 连接兼容性修正：OBS 在回放缓存未启用或不可用时，`GetReplayBufferStatus` 可能返回 604（InvalidResourceState）。连接检查现将 504/604 显示为回放不可用，保留正常连接状态；其他错误仍向上报告。已补充连接诊断及目录恢复回归测试，44 项测试通过，并通过真实 OBS 的密码鉴权验证。

## 阶段 3：手动长片段交接验证

模组菜单新增“测试长片段交接（约 10 秒）”。测试独占启动回放缓存，预留 3 秒画面后启动普通录制，等待 `RecordStateChanged` 的 `OBS_WEBSOCKET_OUTPUT_STARTED` 事件，再等 2 秒后保存回放，继续录制 1 秒后停止。保存回放期间，回放缓存和普通录制均继续运行。已有普通录制或回放缓存时拒绝启动；结束或取消时尝试停止本次启动的输出。连接重建后不会沿用旧连接的输出所有权，需核对 OBS 状态。

测试随后在隐藏 Worker 中探测两份素材，并比较回放尾部和普通录制头部的连续解码画面。至少匹配 0.5 秒、包含 8 个不同帧且只有一个有效对齐时，才报告“已验证”；静止、循环、没有匹配或时间间隔不一致的画面报告“待确认”。测试期间应让 OBS 捕获到持续变化的画面。两种结果都保留视频和报告，供检查或离线重跑。

每次报告位于固定数据目录的 `diagnostics/handoff/<测试编号>/`：

- `capture-state.json`：采集进度、已获得的路径和时钟记录。
- `capture.json`：完成的采集记录，包含两份素材路径、原始请求/事件时间和时钟频率。
- `replay-frames.json`、`recording-frames.json`：比较窗口的帧哈希和 PTS。
- `verification.json`：媒体探测、重叠证据、两份素材中的接缝 PTS、延迟及安全余量建议。
- 发生采集或清理错误时另存 `error.json`、`cleanup-error.json`。

Worker 也可以直接运行采集或离线复核：

```powershell
# 默认连续做 3 轮；密码沿用 CELESTE_REC_OBS_PASSWORD
# 端口不是 4455 时，另加 --endpoint 'ws://127.0.0.1:实际端口'
.\dist\Debug\CelesteRecHelper\worker\CelesteRecHelper.Worker.exe obs-handoff --data-dir 'D:\Games\Celeste\Saves\CelesteRecHelper' --repeat 3 --overlap-seconds 2 --output '.\dist\handoff.json'

# 只复核已有素材，不操作 OBS；请将“测试编号”替换为报告目录名
.\dist\Debug\CelesteRecHelper\worker\CelesteRecHelper.Worker.exe verify-handoff 'D:\Games\Celeste\Saves\CelesteRecHelper\diagnostics\handoff\测试编号\capture.json'
```

`obs-handoff` 必须显式指定固定的 `--data-dir`，支持 `--repeat 1..10`、`--overlap-seconds 0.1..10`、`--endpoint`、`--password-env`、`--output-directory`、`--ffmpeg`、`--ffprobe` 和 `--output`。`verify-handoff` 支持两项媒体工具路径与 `--output`。退出码 0 表示全部验证通过，3 表示至少一项待确认，1 表示运行失败。视频仍保留在 OBS 录像目录或指定的输出目录。

阶段 3 本机验证（2026-11-19）：沿用用户现有 OBS 32.2.2 / obs-websocket 5.7.4、AMD H.264、1920×1080 / 60 FPS、MKV、单音轨、缓存 300 秒 / 512 MB，完成四次实际交接。重叠等待均为 2 秒，连续画面重叠为 4.367、3.800、3.900、3.950 秒，均找到唯一有效对齐。启动延迟 16.5–28.4 毫秒，保存完成延迟 226.9–256.6 毫秒。保留 2 秒等待，安全余量采用 `max(30% × C, 15 秒)`；详细记录见 PLAN 的阶段 3。

构建 0 警告、0 错误；61 项测试通过、无跳过，包括真实 ffmpeg 生成素材的已知重叠/缺口验证、静止和循环画面判定、输出已被占用、缺少运行事件、重连后的所有权检查、取消时的清理，以及启动失败后的自定义录像目录恢复。上述实测验证手动短时交接的画面覆盖；自动阈值触发、最终裁剪、音频接缝和长时间误差仍按阶段 4–6 完成。

## 阶段 4 当前检查点：Core 片段状态机

第一部分已实现 `SegmentStateMachine`，复用现有路线、当前生命、逻辑片段、素材和保存请求模型，清单 schema 保持兼容。它运行在调用方指定的同一线程，只处理状态转换；调用方需在每次状态变化后保存清单，再执行返回的保存指令。

- 画面稳定：仅在路线录制中且没有当前生命时建立起点，重复稳定事件不改变起点。
- 结束键：立即固定区间，返回包含片段编号和 OBS 会话编号的保存指令，下一段从本次结束点开始。无当前生命、画面不可操作、重复的零长度边界或已结束路线均返回明确结果。
- 死亡：清除未确认区间，保留已确认片段及其待完成保存；下一次画面稳定时建立新的生命。
- 通关：由游戏适配层等待结算尾段后传入最终结束时间，记录最后片段并标为已通关；后续离图不会覆盖通关状态。
- 普通离图：标为已结束并丢弃未确认区间；首版保存退出、返回地图、重开、换图共用此规则，新路线由后续游戏适配层创建。
- 保存结果：调度器可将多个已记录片段绑定到一个实际保存请求；同一路线同时只允许一个请求在途。成功时添加素材引用，失败时保留区间和原因；死亡或离图之后仍可完成旧请求，重复/迟到回调不会覆盖已处理结果。

保存成功入口接收 `ObsCaptureSession` 已验证为存在且非空的文件结果，并进一步核对请求、目录、时间和文件是否已被使用。此处不会访问 OBS 或自行查找录像。保存排队、合并触发、超时计时、清单自动写盘及游戏节点适配是后续部分的工作。

本部分验证（2026-11-19）：26 项新增测试覆盖连续片段、无效结束键、死亡/复活、通关/离图、批次共享素材、失败和迟到回调、单调时钟，以及现有持久化/启动恢复兼容性。运行 `.\build.ps1 -SkipDeploy` 后全量 87 项测试通过、无跳过，构建 0 警告、0 错误。本部分尚未接入游戏，核查以 Core 源码与测试为准；用户核查后继续保存调度。
