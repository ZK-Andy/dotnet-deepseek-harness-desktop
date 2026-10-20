# Agent Note: 三组重复收敛为单一事实源（命令帧取值 / bundles 条目等值 / 原子写）

Status: implemented

中文（双语暂不启用；启用时恢复 .md 与 .zh.md 镜像 + .i18n.yaml 配对机制）

## Problem

审计逐字核实了三组重复实现——同一契约在两处以上各写一份，改契约时只改一处即产生漂移：

1. 命令参数帧的 `enabled` 取值：壳侧 `AutostartCommandRouter` 与 `Tray/CloseToTrayCommandRouter` 各有一份同形的 JSON 取值；
2. bundles 条目等值谓词（该条的收敛由 Core 纯度那篇承载，见「Related」）：Core 的
   `ProfilePackageCheck.BundleEntryEquals` 与安装侧的私有副本跨层同名同形；
3. 原子写（同目录临时文件 + 改名覆盖）：安装侧、崩溃取证 marker、关闭行为偏好三处各写一份（临时文件命名与失败清理语义各不相同）。

## Decision

- **命令帧取值**收进 Core 契约层 `Core/Ipc/CommandFrame`（与 `HostCommandIds` 同族）：`CommandFrame.ReadEnabled(ReadOnlyMemory<byte>)` 是唯一入口，两个路由改用它并删各自副本。
- **bundles 条目等值谓词**只有 Core 一份（`ProfilePackageCheck.BundleEntryEquals`）：安装侧复用之，私有副本已不存在
  （该收敛的决策与理由住 Core 纯度那篇，见「Related」）。
- **原子写**收进 Infrastructure 边界层 `Infrastructure/Platform/AtomicFile`（`AtomicFile.WriteAllText(path, text)`）：同目录随机后缀临时文件 + 改名覆盖 + 失败清理临时文件。调用点自带的前置仍留在调用点——marker 的符号链接/重解析点守卫、偏好写前的建目录，都是各调用点的语义而非原子写语义。

## Alternatives considered

- **维持现状不修（三组重复各留一份）**：落败。重复是漂移源：命令帧字段改名、原子写临时文件命名、bundles 等值语义三处都要同步改，漏一处即行为分叉；审计已逐字核实，漂移不是假想。
- **三组合并进一个「工具箱」静态类**：落败。三组分属不同层（跨包契约 / 业务判定 / IO 边界），混装会造出跨层依赖；分开收各自落在本层唯一合适的位置。
- **原子写收进 Core 复用**：落败。原子写是文件 IO，Core 纯度边界（IO 经端口、Core 只留纯判定）不允许。
- **只收前两组、原子写留待后续**：落败。三组同批收敛才能让「原子写只有一个实现」这条判据成立；分批收敛反而多一次跨组漂移窗口。

## Consequences

- 三处重复实现消失：三组契约各只剩一个修改点。
- 原子写语义统一：临时文件名为 `.<文件名>.tmp-<随机后缀>`，写入中途崩溃不留半写目标文件。marker 的临时文件名不再复用 owner token（token 仍只写在 marker 文件内容里，读方契约不变）；关闭行为偏好的临时文件名由固定 `.tmp` 变为随机后缀（并发写不再互相覆盖临时文件）。
- 新增两个共享单点（Core 契约层 + Infrastructure 边界层），依赖方向不变（Presentation → Core/Infrastructure → Core），无跨层新引用。

## Testing

`CommandFrame` 与 `AtomicFile` 各有直接用例（开关帧四种取值形态；原子写落盘、覆盖、失败不留残留）。既有回归（marker 写读往返、偏好持久化往返、profile 清单写回、事务 journal 写回）继续覆盖三个调用点。

## Related

- 审计报告 §6.3 第 1–3 组（`.plan/审计报告-功能闭环与架构审计-2026-10-20.md`）。
- 命令 ID 单一事实源：[2026-10-20-host-command-ids](../architecture/2026-10-20-host-command-ids.md)。
- Core 纯度边界：[2026-10-20-core-purity-boundaries](../architecture/2026-10-20-core-purity-boundaries.md)。
- 配套补测：[2026-10-20-orchestration-test-coverage](../testing/2026-10-20-orchestration-test-coverage.md)。
