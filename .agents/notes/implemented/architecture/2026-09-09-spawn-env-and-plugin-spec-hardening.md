# Agent Note: spawn-env-and-plugin-spec-hardening

Status: implemented

Review: LIGHT/2026-09-09/R2=ok（0 Blocker、2 Suggestion 均为文档口径，已收口）

## Problem

我方 spawn 的子进程原样继承宿主 shell 环境，两条链路各有缺口。

**环境继承**：`HarnessRuntimeHost.BuildStartPsi` 只做加法（`DSH_HOME`、PATH 增补、孤儿 token），不删继承项。宿主 `NODE_OPTIONS=--require …` 会进入 dsh 及其 MCP 下游；`npm_config_registry`/`pnpm_config_*` 会改写全局安装与插件安装的解析来源、构建策略与缓存目录——首次引导的 `npm install -g @deepseek-ai/dsh@alpha` 亦在其列（供应链面）。上游 Electron 宿主在 spawn 处显式过滤（`apps/desktop/src/host-process.ts:108-111`：剔 `NODE_OPTIONS`、`DSH_DESKTOP_*`、`npm_*/pnpm_*/corepack_*`），我方无等价收口。

**spec 形状**：市场/预设 spec 原样进 `dsh plugin add <spec>` 的参数列表。`ArgumentList` 免了 shell 注入，但前导 `-` 仍会被下游解析为 flag。上游有形状 allowlist（`apps/desktop/src/project-manager.ts:162` `packageNameFromSpec`），我方无。当前 spec 只来自自有常量（`MarketSpec = dshmarket@latest`、随包 companion tgz），故这是边界不变量而非已发生的漏洞；目录扩容后即为输入面。

## Decision

**环境净化单点** `EnvironmentHygiene`（`Services/EnvironmentHygiene.cs`）：`IsInheritedNoise` 判定 `NODE_OPTIONS` 与 `npm_`/`pnpm_`/`corepack_` 前缀（**均大小写不敏感**；上游对 `NODE_OPTIONS` 是精确匹配，本壳更严），`StripInherited` 从 `ProcessStartInfo.Environment` 剥离。调用约定 = **先剥离、后写我方变量**。

接线四处，覆盖我方全部 node/npm/dsh 子进程：

- `HarnessRuntimeHost.BuildStartPsi`（dsh web 宿主）
- `MarketInstallHelper.RunPluginAddAsync` 的 `BuildPsi`（`dsh plugin add`）
- `RuntimeVersionGate.BuildProbePsi`（`dsh --version` 探针；否则宿主 `NODE_OPTIONS` 会在探针期执行代码）
- `RuntimeBootstrap.BuildCapturePsi`（引导捕获面：全局 node 探测、npm 全局安装）

**一处刻意偏离上游**：不剥 `DSH_DESKTOP_*`。该前缀承载本壳自有注入——`DSH_DESKTOP_SPAWN_TOKEN` 是孤儿清扫的跨启动复验凭据（ADR self-update-exit-reaps-dsh-child 缺口 B），剥了就失去零误杀复验能力。

**spec 形状 allowlist** `MarketInstallHelper.IsValidRegistrySpec`（`Services/MarketInstallHelper.Spec.cs`，以上游 `packageNameFromSpec` 判据为基，另拒 `link:` 且 `file:`/`link:` 前缀按大小写不敏感匹配）：拒空串、前导 `-`、含空白或反斜杠、含 `://`、`file:`/`link:` 前缀、scoped 缺 `/`；包名过 `PACKAGE_NAME_PATTERN`、版本或 dist-tag 过 `VERSION_PATTERN`（`latest` 等 tag 合法）。

- 校验只在 `PluginSpecOrigin.Registry` 来源生效；随包 `file:` spec 是安装器资源目录自持的内部路径（Windows 安装目录可含空格），不套 registry allowlist。
- 不合法即拒装、留原因日志、返回非零——不抛，保持安装面 best-effort 契约。

## Alternatives considered

- **照抄上游连 `DSH_DESKTOP_*` 一起剥**：落败——会连带剥掉孤儿清扫 token，下次冷启动无法复验 pid 归属，误杀无关进程的风险回来。
- **只净化 `BuildStartPsi`（待办原字面范围）**：落败——`dsh plugin add`、版本探针、npm 全局安装同属我方 node 子进程；半个策略比没有更糟：下一个会话无从判断哪些 spawn 干净。
- **依赖 `ArgumentList` 已免注入、不做 spec 校验**：落败——`ArgumentList` 只免 shell 元字符，前导 `-` 仍被下游当 flag 解析。
- **对随包 `file:` spec 也套 registry allowlist**：落败——安装路径可含空格（Windows），会被空白规则误拒；且该路径由安装器资源目录自持，非用户输入。
- **不剥 `npm_config_registry`（怕挡企业镜像用户）**：落败——env 改写 registry 属供应链风险；`.npmrc` 仍是标准且更可审计的配置通道，失败可见（不会静默装错包）。
- **让 dsh/上游 CLI 自己净化**：落败——净化是壳的 spawn 边界责任，子进程只拿得到我们给的环境。

## Consequences

- 宿主 shell 的 `NODE_OPTIONS` hook 不再进入 dsh、MCP 下游与探针；npm/pnpm/corepack 环境改写不再影响我方安装链路。
- 依赖环境变量配置 registry/代理的用户需改用 `.npmrc`；影响可见（安装失败），不静默。
- `PNPM_HOME` 等合法 `pnpm_*` 变量同样被剥（对齐上游）；本壳不消费它们。
- registry spec 形状不合法时安装被拒并留原因日志，不再透传下游。
- 单一净化点：新增 node/npm/dsh spawn 时须显式接线，评审按 R3 边界完备核对。

## Related

- 上游可吸收点分析（§A3 环境净化 / §A4 spec 收紧）：`.plan/journal/2026-09-09-upstream-desktop-absorb-analysis.md`
- [self-update-exit-reaps-dsh-child](../bug-fix/2026-08-28-self-update-exit-reaps-dsh-child.md)：`DSH_DESKTOP_SPAWN_TOKEN` 不剥的理由。
- [gui-path-enrichment](../process/2026-08-25-gui-path-enrichment.md)：另一条 spawn 环境策略（PATH 增补，只加不减；与本篇互补）。
