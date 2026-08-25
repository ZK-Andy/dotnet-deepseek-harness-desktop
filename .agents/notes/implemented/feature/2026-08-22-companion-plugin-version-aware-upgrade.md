# Agent Note: companion-plugin-version-aware-upgrade

Status: implemented

## Problem

随包插件安装只判「在不在」不判「版本」：`MarketInstallHelper.IsBundleInstalled` 只查 profile 的 `dependencies.<pkg>` + `dsh.profile.bundles`，companion 装过一次后任何后续启动都跳过。而 `dsh-desktop-companion` 仅随壳分发（无 registry、无独立发布渠道），插件修订只能搭壳的新版本走——壳自更新到新版后，profile 里装的仍是旧版插件，外部链接接管等客户端行为停在旧实现。历史上已发生三次手动实机重装才生效（apply 契约修复、双旗共存守卫、`__ryn` 判空）。

## Decision

启动后台随包插件任务增加 **companion 版本感知升级**：已就位时比对随包 tgz 与 profile 已装副本的版本号，随包更新即加入待装清单，复用既有安装管线（单次 spawn `plugin add` + store 注入 + bundles 兜底补写 + `host.Stop()` 交监督器重启加载新版）。

- 版本来源两侧都读现成的 `package.json`，不新增持久状态：随包侧 `PluginVersionCheck.ReadBundledVersion` 用 `System.Formats.Tar` + `GZipStream` 从 tgz 解 `package/package.json`（目录形态直读），已装侧 `ReadInstalledVersion` 读 `<DSH_HOME>/profiles/desktop/node_modules/<pkg>/package.json`（共享 home 切换前的历史版本读 `profiles/web`）。
- 比较复用 `UpdateVersion.Compare` 数字段逐段比；判定 `NeedsUpgrade` = 已装版本不可读（含未装/副本损坏）或随包更新。随包侧结构异常 fail loud 记日志跳过（自家产物坏了必须可见），已装侧异常返回 null 视为未知并走重装修复（profile 可再生）。
- 范围为随包插件全清单（接棒于 `2026-08-25-bundled-plugin-version-aware-catalog`）。
- 检测信号为**版本号**（用户拍板）：改插件必须 bump `plugins/dsh-desktop-companion/package.json` 的 version，否则升级静默不触发。

## Alternatives considered

- **tgz 内容哈希比对**（SHA256 记 profile 状态文件）：落败——免 bump 纪律、任何字节变化可检出，但无版本语义、多一份持久状态且不可读；用户明确要版本号进实现。哈希路线若日后需要可与版本号并存（哈希兜底 bump 遗漏）。
- **仅壳自更新完成后触发**（记录上版壳版本做信号）：落败——需额外持久化壳版本，「随包 bundled vs 已装 installed」本身就是最小充分信号，且顺带修复手动误删 node_modules 副本等异常态。
- **纳入 dshmarket**：落败——上游 registry 管理、市场 UI 可自行更新；闭包内 tgz 只有重新组包才变，感知收益趋零，徒增比对面。（该「收益趋零」前提已被 `2026-08-25-bundled-plugin-version-aware-catalog` 推翻：dshmarket 钉版落后 11 个 minor 且存量 profile 无自更新通道。）
- **不做（维持手动重装）**：落败——三次手动重装实证该场景真实复发，自动化成本低（纯函数 + 一处分支）。

## Consequences

- 收益：壳自更新后的首次启动自动把 profile 旧插件带上新版本，消除手动实机重装；未装/副本损坏的异常态也顺带自愈。
- 代价/风险：依赖「改插件必须 bump version」纪律，忘 bump 则升级静默不触发（缓解：本笔记记录约定；升级发生时有 `[host] 随包插件升级：a → b` 日志行可见，未发生则无日志）；同路径 `file:` spec 重装依赖 pnpm 按 tgz integrity 失效重拉（实机待复核）；升级后与首装同样重启 dsh 进程，页面闪恢复屏属预期。
- 验证：`dotnet test` 147/147（新增 PluginVersionCheck 21 例：tgz 解析/条目名前缀/损坏 fail loud/目录形态/已装副本缺失与损坏返回 null/升级判定边界/unparseable 抛错）；真实闭包 `resources/runtime/dsh-desktop-companion.tgz` 实证条目名 `package/package.json` 与 version 字段存在。

## Related

- `2026-08-21-desktop-shell-companion-plugin`：companion 插件来源、tgz staging 与安装链路。
- `2026-08-22-desktop-shell-self-update`：壳自更新状态机——本机制覆盖其后「新版壳 × 旧版插件」的同步缺口。
- `2026-08-25-bundled-plugin-version-aware-catalog`：范围泛化至随包全清单的接棒笔记。
