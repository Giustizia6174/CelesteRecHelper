# CelesteRecHelper

按 [PLAN.md](PLAN.md) 开发的 Everest 录制助手。当前完成阶段 1：可加载的模组骨架、设置、Core 模型、清单原子读写、启动恢复，以及 Worker 骨架。录制、OBS 连接和剪辑会在后续阶段接入。

## 构建与部署

需要 Windows PowerShell/PowerShell、.NET 8 SDK 或更高版本，以及安装了 Everest 的 Celeste。项目使用官方 `CelesteMod.Templates` 1.0.24 建立，再按计划整理为多项目结构。游戏引用直接使用本机 `Celeste.dll`、`MMHOOK_Celeste.dll`、`FNA.dll`，不会打包游戏程序集。

```powershell
.\build.ps1
# 非 Steam 安装或未能自动发现时：
.\build.ps1 -CelesteDirectory 'D:\Games\Celeste'
# 构建、测试和生成目录，暂不复制进游戏：
.\build.ps1 -SkipDeploy
```

脚本优先使用用户目录 `.dotnet/dotnet.exe`，否则使用 PATH 中的 dotnet。自动从 Steam 库定位游戏，构建四个项目并运行测试，发布 Worker，生成 `dist/<配置>/CelesteRecHelper/`，最后复制到 `<游戏>/Mods/CelesteRecHelper/`。首次安装会完整暂存后移入 Mods，Everest 可自动加载；游戏正在运行时拒绝覆盖已经安装的模组。Worker 在阶段 1 使用依赖框架的发布方式，自包含发行包按计划在阶段 6 完成。

IDE/直接构建时设置 `CELESTE_DIR` 或传入 `-p:CelesteDirectory=...`。Core 和 Worker 不依赖游戏，可分别构建；测试可直接运行：

```powershell
dotnet test tests/CelesteRecHelper.Core.Tests/CelesteRecHelper.Core.Tests.csproj
```

本机验证基线：Celeste 1.4.0.0、Everest 1.6662.0 stable、.NET 8。`everest.yaml` 的 EverestCore 最低依赖暂设为此已验证版本。

## 模组设置

游戏的 Mod 选项中提供启用开关、结束键（默认 F8）、撤销键（默认 F9）、OBS 主机/端口/密码和视频输出目录。开关默认关闭。阶段 1 的按键和开关只是设置入口，尚不触发录制。

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

OBS 目录恢复记录分别保留简单/高级输出原目录和配置档，不含密码。阶段 1 负责保存、读取和提示待恢复记录；阶段 2 接入 OBS 后检查配置档再实际恢复。未成功完成的后台任务列入启动恢复报告，Worker 执行和重试在后续阶段接入。

Worker 骨架支持只读列出路线：

```powershell
.\dist\Debug\CelesteRecHelper\worker\CelesteRecHelper.Worker.exe routes 'D:\Games\Celeste\Saves\CelesteRecHelper'
```

阶段 1 测试覆盖清单跨实例读写、写入失败不破坏旧清单、临时文件隔离、启动恢复、保存匹配歧义、OBS 恢复记录和未完成任务。

本机验证（2026-11-18）：使用 .NET SDK 8.0.428 构建，0 警告、0 错误；23 项测试全部通过。首次部署后，Everest 1.6662.0 的运行日志确认主模组及 Core 程序集加载成功，持久化/启动恢复初始化完成，DebugRC 的模组列表包含 `CelesteRecHelper`。Worker 的帮助和只读路线列表命令已运行通过。
