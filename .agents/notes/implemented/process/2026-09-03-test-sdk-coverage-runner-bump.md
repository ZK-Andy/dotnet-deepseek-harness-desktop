# Agent Note: test-sdk-coverage-runner-bump

Status: implemented

## Problem

测试工程三包停留在旧版，落后上游一年余的版本线：

- **coverlet.collector** `6.0.4` → 最新 `10.0.1`（覆盖率收集器；`XPlat Code Coverage` 数据收集器即来自此包）。
- **Microsoft.NET.Test.Sdk** `17.14.1` → 最新 `18.9.0`（2026-08-11 随 VS 2026 August Update 发布，`dotnet test` 执行器/发现器宿主）。
- **xunit.runner.visualstudio** `3.1.4` → 最新 `4.0.0`（适配器，支持 xunit v2 与 v3 双版本线）。

三者均落后上游多个主/次版本；`xunit` 本体 `2.9.3` 保持不动。上一会话已完成 csproj 变更并实测 build/test 全绿，但未落档——本次收尾：ADR 记录 + 提交。

## Decision

测试工程 csproj 三包直升上游最新版：

- `coverlet.collector` `6.0.4` → `10.0.1`（补标准 `IncludeAssets`/`PrivateAssets` 声明，与 `xunit.runner.visualstudio` 同款，模板规范形态）。
- `Microsoft.NET.Test.Sdk` `17.14.1` → `18.9.0`。
- `xunit.runner.visualstudio` `3.1.4` → `4.0.0`。
- `xunit` 本体维持 `2.9.3`（xunit v3 迁移是独立决策，不在本 bump 范围；runner 4.x 明确向后兼容 v2）。
- 仅测试工程变更，主工程零改动；产品代码/行为不受影响。

升级动机：测试基础设施跟随上游版本线（.NET 10 世代 SDK 宿主 + 覆盖收集器 + 双版本适配器），为将来可能的 xunit v3 / .NET 特性铺垫；三包均向后兼容既有用法，无配置或命令行形态变化。

## Alternatives considered

- **全量升级含 xunit 本体 → v3**：xunit v3 是断代升级（`xunit.v3` 包、运行模型与配置面不同），波及全部测试写法与 CI 命令，须独立立项。落败——本 bump 不夹带。
- **只升 Microsoft.NET.Test.Sdk**：覆盖率收集器与适配器仍旧，未跟上版本线且失去双版本适配器红利。落败。
- **维持现状**：无收益，且旧 Test.Sdk 与 .NET 10 世代工具链的兼容性边际无保障。落败。

## Consequences

- 三包 bump 后验证：build **0 警告**、`dotnet test` **449/449 通过**。
- 覆盖率收集实证：`dotnet test --collect:"XPlat Code Coverage" -- DataCollectionRunSettings...Format=cobertura` 正常产出 `coverage.cobertura.xml`，`line-rate = 0.5216`（≈ 52.2%，与既有 README 基线一致）——coverlet 10.x 数据收集器行为无回归，CI 覆盖率步骤无需改动。
- CI（ci.yml build-test job）沿用既有 `dotnet test` 命令与覆盖率参数，无配置文件需要跟随修改。
- 唯一仓库面改动 = `tests/DeepSeek.Harness.Desktop.Tests/DeepSeek.Harness.Desktop.Tests.csproj`。

## Related

- [ide0005-enforce-via-format-gate](2026-08-31-ide0005-enforce-via-format-gate.md)（implemented）：测试工程 `GenerateDocumentationFile=true` 的配置背景（本 bump 不触碰）。
- [upgrade-ryn-and-dsh-runtime](2026-08-31-upgrade-ryn-and-dsh-runtime.md)（implemented）：上游依赖 bump 收尾的同类先例（版本事实 + Alternatives + Consequences 结构）。
