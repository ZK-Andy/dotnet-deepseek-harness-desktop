# Agent Note: nativeaot-probe-after-composition-refactor（组合根重构后 NativeAOT 兼容性探针）

Status: implemented

## Problem

项目 `<PublishAot>false</PublishAot>`，三条打包流水线（linux/macos/windows）自 v0.1.x 起发运产物一直是 JIT——csproj 显式声明（ADR `publish-aot-jit-alignment` 对齐发运现实）。NativeAOT 此前被两次决策排除：`publish-aot-jit-alignment`（2026-08-28）将 AOT 兼容降为"裕度"；`installer-size-retain-self-contained`（2026-08-30）为降安装器体积否决 AOT——Ryn 依赖反射/服务注册/DI 面，AOT 高风险，macOS 交叉编需关 AOT。

但 AOT 的受益面（免运行时随包 → 体积、冷启动、常驻内存）恰好撞上两项在途关切（冷启动/内存指标、安装器体积）。2026-09-03 拍板：**组合根重构 + TODO 收口后做一次 AOT 探针**，以数据而非推测重开此决策。

## Decision

2026-09-04 在本机（Fedora 44，Linux x64，clang 22.1.8）执行前后对比探针（**探针级**：命令行 `-p:PublishAot=true`，不改 csproj/发运形态）。**结论：不开 NativeAOT，维持 JIT。**

实测（同一 `--export-diagnostics` CLI 冷启动代理，7 次取中位）：

| 轴 | JIT self-contained（发运现状） | NativeAOT | 变化 |
|---|---|---|---|
| 落盘体积（剥 .dbg） | 88M | 16M（二进制 10.2M + 原生库 + wwwroot） | −82% |
| 冷启动 | 0.23s | 0.17s | −26% |
| 峰值 RSS | 41.9MB | 25.7MB | −39% |
| 裁剪/AOT 警告 | — | 0 条（无 IL2026/IL3050/IL3053） | — |

**AOT 兼容性成立**：0 裁剪警告 + 沙箱内全组合根实跑通过——单实例锁 → 桌面 profile → spawn dsh web（到 `127.0.0.1:<port>`）→ Ryn 应用装配（DI）→ 托盘注册 → "Ryn Run 开始" → 干净退出，零 AOT/反射/DI 异常。意外豁免：本项目不在 .NET 进程内动态加载托管插件（dsh 插件是外部 Node/dsh 进程经 IPC），故 AOT 的 single-file/无动态加载限制**不命中**。

**但性能收益被稀释**：冷启动/内存仅发生在 .NET 托管面；该桌面壳运行时大头是 dsh 子进程 + WebKitGTK/webview（独立进程，200MB 级），托管面只占小部分。端到端换算，AOT 的冷启动/内存收益摊到真实启动（~2s）与全量 RSS（~400MB）约 **3–5%**——用户无感。因此 AOT 在此应用 = **体积专项**，性能非收益。

**决定即维持 JIT**：当前安装器/磁盘体积已按 `installer-size-retain-self-contained` 定位为"不冗余携带、接受结构性差距"，非当前硬指标；AOT 的性能收益配不上 CI 变慢 + mac 交叉编冲突 + 验证债。`publish-aot-jit-alignment` 的"AOT 裕度"定位保留。**若将来安装器/磁盘体积成为硬指标**：先 Linux 重启（有真机 + 原生架构 runner 无交叉编冲突），Windows 次之（CI 原生 x64 易开但本地无真机），mac 缓（单 runner Rosetta 交叉编与 AOT 冲突，需拆双 runner 或 mac 留 JIT）。

## Alternatives considered

- **现在（重构前）全量切 AOT**：核心时序尚未重构，若 AOT 又不兼容，两套高风险变更叠加难归因。落败——探针排在重构后。
- **完全不碰 AOT**：错过潜在高收益，且"裕度"定位停留纸面。落败（用户拍板：重构后测一次）。
- **只在文档层面重申维持 JIT**：无数据支持的重复结论。落败。
- **探针后看数据决定；数据支持开 AOT**（本轮考量否决）：实测 AOT 兼容、体积 −82% 且 0 警告，具备可开条件；但性能收益端到端仅 3–5%（webview/dsh 主导），故 AOT 的唯一实质收益 = 体积，而体积当前非硬指标，遂不开。若体积转硬，转先 Linux 重启此比选。

## Consequences

- csproj 维持 `<PublishAot>false</PublishAot>`；探针不改变发运形态。
- 积累数据：AOT 在此应用 = 体积专项（−82%），可作将来"体积转硬"时的直接依据，无需重测。
- 已知成本（本次未承担，仅记录）：mac 单 runner 交叉编与 AOT 冲突；AOT publish 构建时间更长；Windows/mac 无本地真机 → AOT 运行时风险需实机验证（Linux 有真机）。
- `.dbg` 调试符号随 AOT 单独产出（24.9M），发运可剥；诊断/崩溃符号化需另行携带。
- HANDOFF 待办 ③（AOT 探针）勾销。

### 测试

探针方法：`dotnet publish -c Release -r linux-x64 --self-contained true`（JIT 基线）与追加 `-p:PublishAot=true`（AOT）；冷启动/内存用 `--export-diagnostics` CLI 代理 + `/usr/bin/time -v`，7 次取中位（n=7；实验设置 = 同一 CLI 路径、隔离 DSH_DESKTOP_DSH_HOME、隔离 XDG_RUNTIME_DIR）；体积 `du -sh`。CLI 代理只覆盖托管面；全壳端到端（窗口渲染/托盘/webview 桥）需真机复测（复测命令见 HANDOFF 会话结论）。

## Related

- [publish-aot-jit-alignment](../../implemented/simplification/2026-08-28-publish-aot-jit-alignment.md)：csproj `PublishAot=false` 对齐发运、AOT 降为裕度的出处；本探针以实测维持该定位并给出重启条件。
- [installer-size-retain-self-contained](../../implemented/architecture/2026-08-30-installer-size-retain-self-contained.md)：为降体积否决 AOT 的决策；本探针数据（体积 −82%）兑现其"若体积成硬指标重开"的重启口。
- [aot-json-source-generation](../../implemented/bug-fix/2026-08-26-aot-json-source-generation.md)：源生成 JSON 通道（AOT 裕度机制基础）；探针验证其覆盖裁剪面（未触发警告）。
- 待办区 HANDOFF-todos.md：「组合根阶段方法重构」「组合根 TODO 收口」为探针执行前置（均已落地）；「③ NativeAOT 探针」已勾销。
