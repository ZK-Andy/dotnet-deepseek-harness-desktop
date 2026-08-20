# Agent Note: community-targeted-testing-for-mac-x64-and-windows

Status: implemented

## Problem

`mac x64` 与 `Windows` 两档只能由 `CI`（`package-macos.yml` / `package-windows.yml` 的 `macos-latest`/`windows-latest` runner）出包，项目本地没有 `Intel macOS (x64)` 真机，也没有 `Windows` 真机/可重复的手测环境，无法做「真机针对性测试」。此前 `HANDOFF`/`README` 把该项列为 `🟡 已实现但未针对性测试`，实则被当成我们的内部待办，但我们自己客观上无法完成。

## Decision

把 `mac x64 / win` 的**真机针对性测试从内部待办改为「等待社区支持」**：

- **CI 自动化覆盖不变**：`package-macos.yml`（`arm64`+`x64` 单 runner 矩阵）+ `package-windows.yml` 持续出包、自签（`SELF_SIGN=1`）与 `dsh web:` 自检。
- **真机手测交给社区**：本地无 mac Intel x64 / Windows 真机，若社区贡献者/用户在真机验证并反馈，再据证据决定是否显著调整。
- **`🟡` 表意更新**（`README` 中英 + `HANDOFF` 待办）：从「已实现但未针对性测试」改为「已实现且 CI 自动出包，但真机针对性测试等待社区支持」。
- 不改任何打包/CI 代码，纯状态与表意调整。

## Alternatives considered

- **维持为内部待办**：落败——我们没有真机/可重复手测手段，列为"待办"等于一个做不完的空项，且掩盖真实约束。
- **购置/租用 mac Intel 与 Windows 测试机**：落败（暂缓）——引入持续成本与维护负担，当前无此预算与运维资源；社区支持作为低成本过渡。
- **用虚拟化/仿真代替真机**：落败（另议）——`macos-x64` 无法无硬件虚拟化，`Windows` 语义性测试无法代替真机行为验证，价值有限、不在本决定范围。

## Consequences

- 收益：待办清单更诚实——明确「自己做不了、等社区」，避免空挂一项；社区验证路径被明确为其正当来源。
- 代价：`mac x64 / win` 暂无我们的真机背书，风险靠 `CI` 自动化与社区反馈兜底。
- 后续：若社区反馈暴露问题或贡献者接入 test-lab，可据证据把该项重新拉回内部或以证据关闭。
