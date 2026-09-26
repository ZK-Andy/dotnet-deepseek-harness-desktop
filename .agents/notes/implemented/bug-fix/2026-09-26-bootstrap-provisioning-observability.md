# Agent Note: bootstrap-provisioning-observability（供应链输出透传）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok（R1 5 Suggestion、R2 1 Blocker+3 Suggestion、R3 2 Suggestion 全收口）

## Problem

引导开始→`dsh web` 就绪之间是 EnsureNode→`npm i -g`→Verify→插件→起 dsh 链，全程分钟级静默：npm 输出被 `RunCaptureAsync` 吞掉（仅失败可见）、host.log 无步骤锚点、重试循环无编号。mac 5分41秒、Windows 19 分钟零输出，"慢"与"死"不可区分，冒烟心跳也无米下锅。

## Decision

- 默认 hooks 的 `RunProcessAsync` 改走流式捕获：逐行透传 `[bootstrap] <exe>` 前缀（超 300 字截断）+ 截断后累积供失败文案；取消/异常整树击杀语义与 `RunCaptureAsync` 一致。hooks 记录签名不变，测试 fakes 零影响，全部调用点自动受益。
- 行泵与整树击杀不另起炉灶：复用 `PluginProcessRunner.PumpAsync`（加可选截断参，默认不限长，插件侧行为不变）与 `KillTree`（改 internal），同程序集单一实现（R1 简化统一）。
- 下载加首尾行（URL + 落盘字节数）。
- 进度回调同步记 host.log 步骤行；重试循环编号 attempt（开始/失败带耗时）。
- 新增行均为 `[bootstrap]` 前缀诊断行（`verify-ui-copy` 豁免面），不入 UiCopy。

## Alternatives considered

- **hooks 记录加流式成员**：落败——所有测试 fakes 要改；默认实现内部流式化覆盖全部调用点，seam 零变更。
- **只透传到 UI 进度页**：落败——冒烟读的是 host.log，不是 UI；诊断面与展示面必须同时有。
- **下载进度百分比**：落败——`HttpClient` 进度上报要改 hooks 签名；首尾行 + 字节数已足够定位"卡在哪一步"。

## Consequences

- host.log 在引导期持续有行（npm 进度原文透传，外部输出按既有 stderr 惯例直放）。
- 冒烟心跳可直接读 host.log 增量判活。

## Testing

- 新增泵行单测（转发/累积/截断）+ 默认 hooks 接线单测（`dotnet --version` 真跑：exit 0、前转行与累积一致）。
- `RuntimeBootstrapTests` 全绿；真验证在 CI 冒烟日志（步骤行 + npm 透传行）。
