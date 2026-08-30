# Agent Note: step-timeout-test-machine-state-dependence（步超时测试环境依赖——默认全局前缀已有 node 短路复用分支）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

会话基线在真机（本机即真机部署）跑 `dotnet test` 时 `RuntimeBootstrapTests.RunAsync_StepTimeout_FailsAsRetryableError` 失败（448/449）：期望「挂起的下载触发步超时、停在 EnsureNode 步」，实际错误为 `[VerifyDsh] 安装后未能解析全局 dsh 版本（@deepseek-ai...）`——流程根本没有进入下载路径。

**根因**：该测试注入 `ProbeLocalNodeAsync → (null, null)` 模拟「无 PATH node」后，`EnsureGlobalNodeAsync` 还会经 `TryLocateNodeAtPrefix`（简单壳重构 `889df2a` 引入的「复用已装到系统全局的 Node」分支）检查系统全局前缀。默认前缀为 `~/.local`（Unix），而真机上 `/home/zk/.local/bin/node` 真实存在 → 复用分支短路、下载永不触发 → 流程推进到 InstallDsh（hook 假成功）→ VerifyDsh 失败，断言落空。

**为什么 CI 没抓到**：GitHub Actions runner 的 `~/.local/bin/node` 不存在 → 复用分支 miss → 走下载路径 → 超时按预期触发 → 测试通过。v0.4.3 的 449/449 在 CI 机器上成立，但该测试的**结果依赖机器文件系统状态**——重构重写了测试文件（923 行）时，同文件其余走下载路径的测试都钉了 `NodeGlobalPrefix`（`RunAsync_NoGlobalNode_DownloadsAndInstallsGlobal_Succeeds` / `RunAsync_NodeInstallPermissionDenied_PromptsAdmin` 等），唯独此测试漏钉。

## Decision

**给该测试钉临时全局前缀，隔离机器状态、强制走下载路径**——按同文件既有惯例（`NodeGlobalPrefix` 指向 `Path.GetTempPath()` 下的 GUID 临时目录，复用分支 `File.Exists` miss → 下载路径必达，`CancelAfter(0)` 在下载步的 `Task.Delay(Timeout.Infinite, ct)` 上确定性触发步超时）。测试内注释说明钉前缀原因（代码无法自证）。

- 仅改测试（`RuntimeBootstrapTests.cs` 一个用例，+3/-1 行），**零产品代码变更、零行为变更**；测试总数不变（449/449）。
- 产品行为无需改：真机上「默认前缀已有 node 即复用」正是简单壳设计意图（此前安装/用户手动装到系统全局）。

## Alternatives considered

- **产品侧让复用分支可注入/可跳过**：为满足单个测试的隔离需求去扩 `RuntimeBootstrapHooks` 契约（5→6 个委托）或改状态机，工程上过度；钉前缀已让测试完全确定。落败。
- **测试内显式构造「前缀已有 node」+ 断言不下载**：复用分支的正确性已有 `RunAsync_ReusesGlobalPrefixNode_SkipsDownload` 专测覆盖；本用例职责是下载步超时，重复构造复用现场无增量。落败。
- **用 `DSH_DESKTOP_NODE_GLOBAL_PREFIX` 环境变量钉前缀**：选项层（`NodeGlobalPrefix`）更直接且与兄弟测试一致；环境变量是进程级全局，可能泄漏影响并行测试。落败。

## Consequences

- 测试在「默认全局前缀已有 node」的真机（本机即真机部署）上确定性通过，不再依赖机器文件系统状态；CI 与真机行为一致。
- 本次暴露一个 CI 盲区并记录判别经验：环境相关的测试若依赖机器默认状态（默认前缀/环境变量/本机安装件），CI runner 与真机的差异可能掩盖回归——判别/根治手法见 cookbook `[调试]` 段条目。

## Related

- [simple-shell-single-global-dsh](../architecture/2026-08-31-simple-shell-single-global-dsh.md)：`TryLocateNodeAtPrefix` 复用分支与默认前缀 `~/.local` 由此 ADR 引入，是本测试环境依赖的诱因。
