# Agent Note: runtime-deps-upgrade-and-arm64-resume

Status: implemented
Archived: 2026-08-27

## Problem

三处供给变化堆积成一次批量升级：①上游 Ryn 发布 v0.30.3/v0.30.4——v0.30.3 含我们提交的 `IRynWindow.IsMaximized`（PR #75），v0.30.4 起 `Ryn.Interop` 供给 `runtimes/linux-arm64/native`，解除了 2026-08-24 的 linux-arm64 停发条件；②捆绑闭包 Node 钉在 22.23.1，LTS 线已前进至 24.x；③闭包钉版 `dshmarket@1.15.0` 对 npm latest（1.29.2）落后 14 个 minor——正是版本感知升级 ADR 指出的「钉版经常落后」问题在本仓自身的重演。

## Decision

1. **Ryn 三包 bump 至 0.30.4**：`Ryn` / `Ryn.Plugins.Tray` / `Ryn.Ipc.Generator` 统一对齐同 tag 版本；`IRynWindow.IsMaximized` 成为托盘唤回最大化的数据源（见 [tray-recall-maximize-and-check-feedback](../bug-fix/2026-08-24-tray-recall-maximize-and-check-feedback.md)）。
2. **linux-arm64 打包复发**：`package-linux.yml` matrix 补回 `linux-arm64`（`ubuntu-24.04-arm`）；`release-preflight.sh` 资产矩阵补回 `*_linux-arm64.deb` 与 `*_linux-aarch64.rpm` 两行；原「x64 job 探测位」步骤完成使命删除；README 双语与 user-guide 双语平台表、architecture 打包节同步。
3. **闭包升级**：Node `22.23.1 → 24.19.0 LTS`（Krypton）；`dshmarket 1.15.0 → 1.29.2`（pnpm 安装、官方 tgz 直拉、本地回退 find 路径三处同步）。本地 `bundle-runtime-ci.sh linux-x64` 全链路重建 + `dsh web:` 自检通过为合入门槛。

## Alternatives considered

- **维持 arm64 停发到首个用户报告再恢复**：落败——供给已实证（nupkg 内 `runtimes/linux-arm64/native` 在案），停发的唯一前提消失；CI 冒烟可直接把关包质量，无需等用户当试验品。
- **Node 升至 current（26.x）而非 LTS**：落败——闭包是发布产物的确定性底座，LTS 线是唯一的支持承诺；current 线半年一换不可持续。
- **dsh 同步升级**：npm latest/next 均仍为 `0.1.1-rc.2`，无可升目标；`RuntimeVersionGate.MinimumVersion` 底线随之不动。
- **pnpm 同步升级**：维持 11.7.0 钉版——11.22+ 的严格 node-semver 预发布解析曾致三平台构建失败（见 [pin-pnpm-1170-for-bundled-closure](2026-08-21-pin-pnpm-1170-for-bundled-closure.md)），无新证据前不动。

## Consequences

- 三平台闭包缓存 key 含 DSH_VERSION/NODE_VERSION/companionSha，本次 node+dshmarket 变更使各平台首跑全量重建（预期成本）。
- dshmarket 大版本跨 14 个 minor，市场 UI/安装链行为差异由 CI 冒烟与实机验收覆盖；后续漂移巡检仍属 freshness A 线议题（未立项）。
- linux-arm64 包首次过冒烟链（arm runner 上 deb/rpm 安装 + `dsh web:` 探活），GUI 渲染级验证仍靠社区/实机。

## Related

- [产物验证链](2026-08-24-artifact-verification-chain.md)：arm64 停发/恢复的原始记录位。
- [tray-recall-maximize-and-check-feedback](../bug-fix/2026-08-24-tray-recall-maximize-and-check-feedback.md)：Ryn bump 的主要消费方。
- [随包插件版本感知目录](../feature/2026-08-25-bundled-plugin-version-aware-catalog.md)：钉版落后问题的机制面分析。
- [pnpm 11.7.0 钉版](2026-08-21-pin-pnpm-1170-for-bundled-closure.md)：维持不动的理由。
