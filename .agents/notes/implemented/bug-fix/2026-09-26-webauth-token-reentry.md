# Agent Note: webauth-token-reentry（鉴权页自愈重进）

Status: implemented

Review: FULL/2026-09-26/R1=ok R2=ok R3=ok

Related: 兑现 [`smoke-settle-content-verdict`](../testing/2026-09-26-smoke-settle-content-verdict.md) 的"内容断言留产品侧跟进"。

## Problem

mac-arm64 落定后截图仍是 auth 文本：token 第二跳真实提交过（6 到达含 token 跳），但终页是 dsh 的 401 文本——token→303→cookie 链在 WKWebView 未落定【推断·未证；Ryn DataStore 根因 track 并行】。到达（传输层）全绿而内容是坏的，冒烟已证明到极限，壳必须自己把终页扶正。

## Decision

- Core 新纯策略 `WebAuthRecovery`（`AuthRequiredMarker = "authentication required"` + `MaxReentries = 1`）：可见文本命中标记且重进未用完 → 重进，否则放行/放弃。标记耦合上游固定英文串，注释写明来源与误伤面（正常 DSH UI 不含该串）。
- `EnterMainUiAsync` 第二跳提交后探可见文本（400 字截断只读探针）：命中则有界重进 token URL 一次（复 present 同一 token URL——401 页自带"重开 URL"指引即此语义；不是猜新 token，per-process token 不可猜；复用证据：host 会话 curl 同一 token 两次 303【探索性，n=2，未留 artifact】），再坏只 fail loud（`[nav] 鉴权页自愈失败`）不挡启动、不循环。
- 探针 15s 超时（`AuthProbeTimeoutSeconds`，x64 病 renderer 超时 30s 的前车之鉴）：超时/异常按 Unknown 跳过自愈，绝不拖死启动。
- 导航调用前后加诊断行（发起/返回）：x64 腿疑似 `NavigateAsync` 挂起（hop2 未发出），下次实跑直接定位卡点，零行为变更。

## Alternatives considered

- **自愈失败即阻断启动**：落败——dsh 在跑、页可读（auth 页自带"重开 URL"指引可操作），挡启动更差；loud 日志 + 截图留证足够。
- **无限重进直到好**：落败——cookie 根本性缺失时变无限导航循环；一次是"扶一把"，多次是"赌博"。
- **冒烟侧 OCR 断言内容**：落败——runner 无 OCR；壳侧自愈 + loud 日志是现有条件下唯一可达 rung。
- **等 Ryn DataStore 根因再修**：落败——上游联动周期不可控；自愈与诊断先行，根因 track 并行。

## Consequences

- 组合根只接线（R1）：决策在 Core 类型，`DesktopBootstrap.App` 仅编排探针/导航/日志。
- mac-arm64 下次 tag 跑应拍到好页面；x64 病腿探针超时即过，不新增挂起点。

## Testing

- `WebAuthRecoveryTests` 文本×次数矩阵（null/空/正常/auth × 0/1/2）。
- 接线薄层沿既有先例不单测；真验证在 CI mac 冒烟截图。
