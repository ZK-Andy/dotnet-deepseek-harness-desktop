# Agent Note: 行为落定门（转发模型验证对齐新链路）

Status: implemented
Erratum: 2026-09-28 — 落点换家：正文所指 `scripts/smoke-settle-lib.sh` 已随「脚本层收口」拆并——等待与落定面迁至 `scripts/lib/smoke-wait-lib.sh`，判定/证据面（结论行/存活门/像素见证/证据打印）迁至 `scripts/lib/smoke-verdict-lib.sh`；同批三平台入口与 rpm 容器腿改为 source 共享库并拆出 `scripts/smoke-linux-rpm-inner.sh`（见 [script-layer-consolidation](../process/2026-09-28-script-layer-consolidation.md)）。正文不动。

Related: `architecture/2026-09-27-loopback-forward-proxy`（转发模型）+ `bug-fix/2026-09-27-shell-mint-and-forward`（铸币）+ `testing/2026-09-26-page-verdict-gate`（裁决）+ 上游 `packages/client/connection/src/browser-auth.ts`（`authorizeIndex` 303 铸币，已验实）+ Ryn `saucer_webview_on` 导航回调 + dispatch `36300876224`（双腿同签名实证）。

## Problem

转发模型落地后 CI（`package-macos` 双腿）恒红同一行：`落定超时（90s 内未见 token 第二跳且①后无壳到达；到达 2 次）`，而代理流量证明真 UI 在跑、截图人眼全对。三轮修代理/截图/存活全在门下游——门本身与新链路无交集：

- 落定门等导航提交，但新链路零 host 导航（`EnterMainUiAsync` 只铸币；holder 靠 renderer 自 `location.reload()`，不产生到达回调）。
- token 第二跳恒假（token 永不进导航靶点，铸币走纯 HTTP）。
- `verdict healthy` 把 holder 判绿（同源 + 非空；43 字 holder 实证），健康监视器同样把 holder 判 Alive（`text:43`）——产品侧无 holder/真 UI 区分信号。
- “到达 2 次”= 初载 holder 1 次 × OUT/host.log 双源双计（时间戳致 `sort -u` 失效）。
- holder 单次 fetch 无重试，断一次永驻（且被报 healthy/Alive 误绿）。
- FAIL 分支只打 `tail -30`，关键行被代理日志淹没，排查恒盲。
- 注释多处引用上游 `apps/desktop/src/web-document.ts`（`serveWebDocument`/`onBeforeSendHeaders`），现存上游快照全仓无此文件，无法复核。

## Decision

- 落定谓词重写（`scripts/smoke-settle-lib.sh`，三脚本同源）：落定 = ① + 铸币 303 行（`MintAsync` 成功唯一机器信号）+ 客户端存活（`smoke_client_alive` 提拔为门的行为支）。到达只诊断回显；verdict 只取 auth 硬拦（401 真坏页），healthy/unknown/缺行交存活 + 见证判定。
- `smoke_client_alive` 计入 WS 隧道（`remote.mux` 无 200 行，纯 WS 形态会漏数）；阈值仍 ≥3。
- 双源去重前剥时间戳（`strip_ts`）；FAIL 分支显式打印到达/铸币去重行（`echo_nav_lines`/`echo_mint_lines`）；Linux `wait_url` 与 rpm 容器 `LOG` 指到同一日志（同文件双读经去重归一）。
- holder 标记排除（产品侧）：`Core.WebAuthRecovery.HolderMarker` 为 holder 文本唯一家，`DshLoopbackLocal.HolderPage` 首行复用该常量；`ClassifyDetail` 含标记即 Unknown（鉴权优先，auth 不被掩盖）。fail-open 方向：Unknown 交存活 + 见证，不挡启动。
- holder 自恢复无计时器（禁加时）：失败先有界即时重 poll 3 次，再只由 `online`/可见性恢复/手动重试链驱动，`busy` 守卫防并发；`display:none` 重试链不进 innerText，不污染探针。
- R7 正名：删 4 处不可复核的上游文件名引用；铸币对齐改引已验实的 `browser-auth.ts authorizeIndex`，隧道握手术述为标准反代语义。
- 自测夹具同步新语义：ok=①+铸币+存活；新增缺铸币/缺流量/纯到达失败锁、双计去重锁、隧道存活锁；删 token 跳夹具。

## Alternatives considered

- **verdict 要求 healthy（Linux 旧 deb 腿语义）**：落败——探针与 reload 赛跑，常采到 holder；R1 修后 holder 为 unknown，硬要 healthy 即把偶发探针时序变成门红。
- **健康监视器 `Parse` 排除 holder**：落败——健康探针只回长度（`text:<n>`），无文本可判；且 holder 是瞬态加载态，Alive 可接受，卡死态由 R5 重试解决；改探针格式牵连快照/恢复预算，不值。
- **保留 token 第二跳（直连回归即用）**：落败——既定方向跟转发模型，无回归计划；死分支只会误导下一次排查（本次已误导三轮）。
- **holder 用 `setTimeout` 轮询重试**：落败——用户禁加时令；事件驱动 + 有界即时重试已覆盖中断形态，长轮询正常持有不需要心跳。
- **存活阈值提高/像素阈值重调**：落败——浅色 UI 与白墙像素重叠已实证不可分；行为计数 ≥3 沿用实测量级，不动数字。

## Consequences

- `wait_settled` 签名不变（`$1=pid`），三脚本主流程零改；州：166 + 483 + 182 = 831 全绿，三脚本自测全绿。
- `PAGE_VERDICT_REQUIRED` 残留变量删除（设而未读，注释与实现已漂移）。
- 下次 CI 红：FAIL 段直接给出达/铸币去重行 + 缺信号点名（`settle_missing`），不再盲挖。
