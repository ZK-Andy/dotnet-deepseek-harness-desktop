# Agent Note: 插件安装后启动体检探针

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

插件 `dsh plugin add` 成功不代表插件树还能起：坏插件（安装中断、不兼容版本、manifest 损坏）会让下一次 dsh 启动失败或卡死，用户直接遭遇「装完起不来」。官方 Electron 端的 healthCheck hook 用「换血前先证明能活」结构性消除这一面（staging→探针→切换）；我们的 ReconcileProfile 只清不可解析的 `file:`/`link:` 引用，对「registry 依赖已装但启动即炸」无能为力，且只在启动前跑一次，装后从不复验。

壳侧插件安装有两个驱动面，都发生在 spawn dsh 前（batch-1 纪律：绝不「安装后重启」）：随包插件 spawn 前安装（`InstallCompanionBeforeSpawn`）与首启引导的插件相（`InstallBootstrapPluginsAsync`：随包 + preinstall 市场）。安装成功后紧接着的 dsh spawn 就是用户可遭遇「起不来」的时刻。

## Decision

插件安装驱动面报告「本轮确有插件装成功」时，spawn 正式 dsh 前先跑一次**体检探针**（`Services/PluginInstallProbe`，对齐官方 healthCheck 的「换血前先证明能活」廉价子集）：

- **探针 = 独立 spawn 一次 dsh web**（`--port 0`，psi 复用 `HarnessRuntimeHost.BuildStartPsi` 单一事实源：同一 env 净化、PATH 富化、血统 token），等 `dsh web:` URL（60s 时限，与主 spawn 同宽），随后整树击杀探针进程。URL 出来了 = 插件树可加载、web-app 可起，正式 spawn 照常。编排与 psi 构造在 `Services/PluginInstallProbe`；spawn 执行（`RunProbeAsync`）归 `PluginProcessRunner` 进程边界单点。
- **探针失败（超时/早退/无 URL）** → `DesktopProfileBootstrap.ReconcileProfile` 清一次不可解析引用 → 重试探针一次。通过 = 自愈完成；二次仍失败 = 记响亮日志后放行正式 spawn（失败由既有恢复链路兜底——探针是 best-effort 增强，绝不阻断启动）。
- **触发条件 =「本轮确有插件装成功」**：两个安装驱动（`EnsureBundledPluginsBeforeSpawnAsync`/`EnsureMarketFromRegistryAsync`）改为返回是否装成功，调用方据此决定是否探针——无安装的常规启动零探针成本，保持启动时延不变。

## Alternatives considered

- **全量 staging 安装**（官方完整形态：装到 staging profile、探针验证后原子切换）：落败为本次范围——牵动 MarketInstallHelper/ReconcileProfile/profile 迁移三处既有设计（大件，独立待办「事务化插件管线」），探针是其廉价子集、先行落地且不与 staging 冲突。
- **探针失败即阻断启动并出错误页**：落败——探针失败时正式 spawn 大概率同样失败，既有恢复页/监督器已给出人可读的失败面；探针只做「能自愈则自愈」，阻断权不归它（误杀面：探针偶发超时而正式 spawn 本可成功）。
- **复用正式 spawn 失败后的恢复链替代探针**：落败——恢复链在失败后才介入，用户已承受一次完整失败启动（42–47s 插件树加载 + 恢复页）；探针把失败提前到用户看到主界面前消化。

## Consequences

- 收益：「装完起不来」从用户可遭遇变成壳可自愈（坏 `file:` 引用类自动 reconcile 修复）；其余坏插件至少在日志留下「探针二次失败」的明确归因信号，不再是「dsh 静默起不来」。
- 代价：安装轮次的启动多付一次探针 boot（正常 ~8–13s，超时上限 60s）——仅在本轮确有插件装成功时发生（首启/种子归化/坏件自愈轮），常规启动零成本。
- 探针进程被血统 token 标记（复用 `BuildStartPsi`），击杀失败的残留属血统服务端形态，下次启动被 `HarvestLineageResidue` 收割——不新增孤儿清理盲区。
- 非目标：registry 依赖损坏（非 `file:` 形态）探针无法自愈，只报不修——根治属事务化插件管线（staging 隔离坏件）。

## Testing

- 纯逻辑：`VerifyAfterInstallAsync` 三分支（首探即过 / reconcile 后重试过 / 二次失败放行）+ `BuildProbePsi` 形状（dsh 命令、`--port 0 --no-open`、DSH_HOME、血统 token、CreateNoWindow）——xunit 注入 fake 探针与 reconcile 断言调用序。
- 两个安装驱动返回值语义：装成功 true / 全失败或无待装 false（既有用例改断言）。
- 真实 dsh 冒烟（`DSH_TEST_E2E=1` 门控）：探针在隔离 home 上出 URL 后进程被回收。

## Related

- [spawn 环境净化与插件 spec 校验](2026-09-09-spawn-env-and-plugin-spec-hardening.md)：探针 psi 复用同一净化与 spec 防线。
- [端口等待压缩](2026-09-13-port-wait-compression.md)：探针用 `--port 0` 避开首选端口，不与 bind 探测/交接处置交互。
- 事务化插件管线（HANDOFF-todos 大件）：staging 全量形态，本探针是其子集与前置。
