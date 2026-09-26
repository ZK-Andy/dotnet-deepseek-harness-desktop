# Agent Note: npm-global-bin-path（装机 npm 前缀补 PATH）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

## Problem

Windows CI 实证：`npm install -g` 退出码 0（512 包，5 分钟），随后 `VerifyDsh` spawn `dsh` 报"系统找不到文件"。根因不在 npm——`VerifyDshAsync` 走进程 PATH 查 `dsh`，而 PATH 补齐（`PrependPathToProcessEnv`）只发生在我方前缀（`TryResolveActiveNodeBinDir` 命中才补）；runner 用预装 node（PATH 复用分支）时我方前缀根本没装 node，补齐 no-op，预装 node 的 npm 前缀从没进过进程 PATH。旧注释把该形态记为"指引用户核对"（推给用户）；CI 无用户即等到冒烟超时，真实用户则是困惑的失败页。

## Decision

- `InstallDsh` 成功后、Verify 前：用装机 node 的 npm 本地查询前缀（`npm config get prefix`，无网络），Windows 取前缀根、Unix 取 `prefix/bin`，存在即 `PrependPathToProcessEnv`，查不到沿用既有 PATH（Verify 照常指引失败，不静默）。
- 查询走既有 hooks（fakes 兼容：旧 fake 把查询答成版本号 → 目录不存在 → null，老测试零改动）；OCE 不吞，调用链收口；`report` 同步步骤行（host.log 自动有锚点）。
- 同属 InstallDsh 步骤语义（不新增 `BootstrapStep` 枚举值——步骤序是引导页单一事实源，不动）。

## Alternatives considered

- **Verify 失败文案里教用户手动加 PATH**：落败——现状即如此，CI 场景无解；能确定性解析就不要推给人。
- **安装时加 `--prefix` 强制统一前缀**：落败——改写用户全局 npm 布局，越界；只读查询 + 进程内补 PATH 零副作用。
- **新增 BootstrapStep.ExposeNpmBin 步骤**：落败——枚举值驱动引导页步骤序，加值要动 UI 映射；复用 InstallDsh 步骤行足够定位。

## Consequences

- 预装 node + 自定义 prefix 的真实用户同愈（原 R2#2 边界关闭）。
- Windows CI 仍受 npm 耗时（~5 分钟环境税）约束，但不再死于 PATH。

## Testing

- `NpmPrefixExposeTests`：存在/缺失/坏查询三态 + 端到端 PATH 断言（现场恢复）。
- 既有 `RuntimeBootstrapTests` 全绿（新步骤经旧 fakes 走 null 分支）。
