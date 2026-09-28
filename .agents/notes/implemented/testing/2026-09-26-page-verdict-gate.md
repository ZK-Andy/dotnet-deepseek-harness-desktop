# Agent Note: page-verdict-gate（冒烟绿必须由终页裁决背书）

Status: implemented

Erratum: 2026-09-28 — 落点换家：正文所指 `scripts/smoke-settle-lib.sh` 已随「脚本层收口」拆并——等待与落定面迁至 `scripts/lib/smoke-wait-lib.sh`，判定/证据面（结论行/存活门/像素见证/证据打印）迁至 `scripts/lib/smoke-verdict-lib.sh`；同批三平台入口与 rpm 容器腿改为 source 共享库并拆出 `scripts/smoke-linux-rpm-inner.sh`（见 [script-layer-consolidation](../process/2026-09-28-script-layer-consolidation.md)）。

> 吸收合并（2026-09-28，E 批 ADR 裁定）：`smoke-settle-content-verdict`、`settle-gate-and-probe-retry`、`verdict-honesty-repair` 三篇并入本篇后删除。三篇的落定门口径迭代史（token 第二跳 → token OR ①后到达≥2 → auth 否决+见证 → ①+铸币303+存活）不再留独立篇，现行谓词由 [shell-settle-behavior-gate](../bug-fix/2026-09-27-shell-settle-behavior-gate.md) 拥有；三篇的存活决定收编进本篇 Decision（各条注明来源）。`smoke-settle-content-verdict` 的心跳/超时回退门/`SMOKE_WAIT` 单轮预算与 `windows-bootstrap…`（并入 windows-dsh-spawn-unified）的「停滞不触发②回退」的现行家在 `scripts/lib/smoke-wait-lib.sh`（机制注释就近留脚本）。

Review: FULL/2026-09-26#2/R1=ok R2=ok R3=ok

Related: [`bug-fix/2026-09-27-shell-settle-behavior-gate`](../bug-fix/2026-09-27-shell-settle-behavior-gate.md)（现行落定谓词）+ [loopback-forward-proxy](../architecture/2026-09-27-loopback-forward-proxy.md)（转发模型，expectedOrigin 的现行来源）。

## Problem

冒烟的"绿"若由导航到达背书，则只是传输层事实：WebKit 的提交回调早于新页出像素，截图会拍到上一跳旧像素；而应用对终页内容的判定只写在诊断行里，`unknown` 被当成放行——"verdict 绿配 401 图"能连续通过三跑，人眼不查 artifact 无从发现。

第二处陷阱在采样入口：`EvaluateJavaScriptAsync` 的回报是 **JSON 文档**——页面返回字符串时到宿主是带引号与转义的 JSON 字符串字面量（Ryn 桥内 `JSON.stringify`）。任何按字段比较的判据（同源比较即是）必须先解掉这层壳，否则判据恒不成立。

## Decision

- 采样形态（产品）：探针脚本返回 `location.origin` + `Core.PageProbeSample.Separator` + 可见文本（400 字截断），分隔符编译期由常量拼入脚本，两侧不可能漂移；`Separator` 由单测钉住 JS 字面量安全字符集。
- 桥回报解码（产品）：`PageBridge.RynProbeValue.Decode` 作采样入口——JSON 字符串还原转义、裸值原样返回、JSON `null`/空 → 无值；不用 `Trim('"')`（转义残留、把转义原文当文本）。
- 终页裁决收敛为唯一纯函数：`Core.WebAuthRecovery.Classify(rawSample, expectedOrigin)` 三态（同源 + 文本非空 + 不含鉴权标记 → `healthy`；命中标记 → `auth`；其余 → `unknown`），`ClassifyDetail` 同判并带出 origin/长度供留痕。重进只做一次，由编排形状（探针→至多一次重进→再探针）保证，无计数常量。
- 唯一留痕：落定期只写一行 `[nav] 页面裁决=<token>`（记实际 origin 与文本长度，不记内容；origin 不含 token），token 常量由应用侧 `Core.WebAuthRecovery.Verdict{Healthy,Auth,Unknown}` 拥有，冒烟正则消费同一串——改 token 即显示腿缺行转红（fail loud）。
- 门禁（现 `scripts/lib/smoke-verdict-lib.sh`）：`page_verdict_state` 只认**最后一条**裁决，重试期旧行不遮新行；`auth` 立即 FAIL（任何腿）；置位腿要求 `healthy` 或截图内容见证——外部 origin 上 Ryn 桥把 eval 回包 POST 到页面 origin（桥 `_ipcBase` 默认空串）打不到宿主，该页 DOM 探针恒超时，故以像素见证兜底：近空白（401 墙）与深色引导页判失败，真 UI 通过。
- 截图时序：裁决通过后再等 `SMOKE_REPAINT_SECONDS`（默认 3s，仅在有显示时）才拍，避开上一跳旧像素；睡后补一次进程探活留痕（不改 rc）。
- 探针有限重试（自 `settle-gate-and-probe-retry` 并入）：`ProbePageSampleAsync` 超时/失败有界重试，总尝试 `RuntimeTimeouts.AuthProbeAttempts`（默认 2，初次 + 1 次重试），成功即返、耗尽回 null（Unknown 放行语义不变）；尝试数进配置模型（可调参数禁硬编码）。
- 同步原生调用隔离（自 `verdict-honesty-repair` 并入）：三处宿主导航点（收养恢复/健康 reload/鉴权重载）的 `NavigateAsync` 以 `Task.Run` 包裹再 `WaitAsync(NavCallTimeoutSeconds)`——Ryn 底层同步 `set_url` 可原生 hang（非托管阻塞、不可取消），异步计时器挂不上；线程池线程卡死可被计时器解绑（泄漏一线程 + loud 可接受）。`Ryn#101` 按当时的错误模型所报，已关闭认错——复发须附新证据**重开新 issue，不复用已关闭的 `Ryn#101`**。
- 绿跑留尾 + 目检纪律（自 `verdict-honesty-repair` 并入）：PASS 也打印应用日志尾部 30 行再删（现 `smoke-verdict-lib.sh`）——绿跑的导航/探针/自愈行可查，打破"绿即无证"；verdict 结论必须附当轮截图目检——门只证到达/裁决，内容归人眼，两者齐才算数。

## Alternatives considered

- **截图 OCR/像素断言内容**：落败（三度否决）——runner 无 OCR；像素断言随主题/缩放/字体漂移，脆。
- **只加 "auth 必红"、保留到达即绿**：落败——`unknown` 仍绿，假绿换皮；要的是显示腿"绿 = 同源 + 非空 + 未命中鉴权标记的一次采样"。
- **healthy 再加正向 UI 标记**：落败——UI 文案随上游版本漂移，机器判据会随上游改字假红；真 UI 归裁决后截图人眼，机器只证不变量。
- **冒烟侧直连 dsh HTTP 复现 303→cookie**：落败——看不到 WebView 的 cookie jar 与 DOM，WebView 里已渲染好 UI 时会误红；只能作服务端补充信号。
- **在冒烟脚本里自行解析 DOM**：落败——脚本无 WebView 通道，唯一 DOM 读点是应用自身探针。
- **回滚落定门到 token 行**：落败——退回 Linux 恒红，用门红掩盖产品时好时坏；诚实红比恒红有用。
- **坏页不进门、只靠人眼**：落败——确认坏页机器可判（`auth` 门），人眼负责"绿是否真好"，机器负责"坏必须红"。
- **`Task.Run` 改专用线程/取消原生调用**：落败——原生 hang 不可取消，专用线程与池泄漏等价；池 + loud 最简。

## Consequences

- 交付不变量（一次采样）：同源 + 可见文本非空 + 不含鉴权标记 ⇒ `healthy`，显示腿据此判绿；真 UI 仍由裁决后截图人眼判（机器不判像素）。
- 401 与 `auth` 必红；落定失败不留任何放行路径——冻结豁免与窗口夹紧已随 [settle-window-and-arm64-freeze-retirement](2026-09-28-settle-window-and-arm64-freeze-retirement.md) 退役。
- 代价：探针 15s×2 与两跳超时吃落定窗；同源但采到空文本（提交后尚未渲染出内容）判 `unknown` → 红——宁可红一次，不让"还没渲染"冒充绿；绿跑 CI 日志 +30 行；hang 场景悬空池线程。

## Testing

- Core 单测：`Classify` 三态（同源/非同源/空文本/标记大小写/文本内含分隔符只切首个）、`ClassifyDetail` 明细、`PageProbeSample.TrySplit` 残缺采样、`Separator` JS 字面量安全；Desktop 单测：`RynProbeValue` 桥真实形态（JSON 字符串/转义还原/裸值/JSON null）。
- 冒烟自测：`settle-auth-fails`、裁决三态/缺行/塌陷/恢复、`wait_url` 矩阵等夹具族（见 `scripts/lib/smoke-verdict-lib.sh` 与各入口 `--self-test`）。
- 历程 dispatch 痕迹：run `36232183186` / `36233266978` / `36237256739` / `36240692140` 为 Problem 各机制的实证 run，可查不占正文。
