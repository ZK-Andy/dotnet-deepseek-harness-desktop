# Agent Note: page-verdict-gate（冒烟绿必须由终页裁决背书）

Status: implemented

Review: FULL/2026-09-26#2/R1=ok R2=ok R3=ok

Related: 直接起因是 dispatch run `36240692140`（`ec515cc`）的 artifact 复核——amd64 job 绿、上传截图却是 `dsh web authentication required; reopen the URL printed by dsh web.`；对照 run `36233266978`（`3c03fc4`）截图是真大厅 UI 而 job 红。两跑差的是落定门：旧门要求 token 第二跳（Linux 永不出现）故恒超时，截图拍得晚；新门改认「①后双到达」后抢在第二跳提交回调上立即开火，拍到的是第一跳裸 origin 的 401 旧像素。落定门与截图时序互为镜像的两个假象。

## Problem

冒烟的"绿"由导航到达背书，而到达只是传输层事实：WebKit 的提交回调早于新页出像素，`import` 在回调后立即拍，拿到的是上一跳（裸 origin）的 401 帧；应用对终页内容的判定只写在诊断行里（`页面内容正常…免自愈` / `页面探针未知…按未知放行` / `鉴权页自愈失败…`）：`healthy` 与 `unknown` 两态没有机器可判的终态，且 `unknown` 被当成放行。结果是"verdict 绿配 401 图"能连续通过三跑，人眼不查 artifact 就无从发现。

第二处隐患在采样入口：`EvaluateJavaScriptAsync` 的回报是 **JSON 文档**——页面返回字符串时到宿主是带引号与转义的 JSON 字符串字面量（Ryn 0.38.0 桥内 `JSON.stringify`；仓内先例 `PageHealthMonitor.Parse` 夹具兼收 `text:42` 与 `"text:42"`）。任何按字段比较的判据（同源比较即是）必须先把这层壳解掉，否则判据恒不成立。

## Decision

- 采样形态（产品）：探针脚本返回 `location.origin` + `Core.PageProbeSample.Separator` + 可见文本（400 字截断），分隔符编译期由常量拼入脚本，两侧不可能漂移；`Separator` 由单测钉住 JS 字面量安全字符集。
- 桥回报解码（产品）：新增 `PageBridge.RynProbeValue.Decode` 作采样入口——JSON 字符串还原转义、裸值原样返回、JSON `null`/空 → 无值；不用 `Trim('"')`（转义残留、把转义原文当文本）。
- 终页裁决收敛为唯一纯函数：`Core.WebAuthRecovery.Classify(rawSample, expectedOrigin)` 三态（同源 + 文本非空 + 不含鉴权标记 → `healthy`；命中标记 → `auth`；其余 → `unknown`），`ClassifyDetail` 同判并带出 origin/长度供留痕（采样只拆一次）。重进只做一次，由编排形状（探针→至多一次重进→再探针）保证，无计数常量。
- 唯一留痕：落定期只写一行 `[nav] 页面裁决=<token>`（记实际 origin 与文本长度，不记内容；origin 不含 token），token 常量由应用侧 `Core.WebAuthRecovery.Verdict{Healthy,Auth,Unknown}` 拥有，冒烟正则消费同一串——改 token 即显示腿缺行转红（fail loud），未置位腿丢 `auth` 门。
- 门禁（`smoke-settle-lib.sh`）：新增 `page_verdict_state`（只认**最后一条**裁决，重试期旧行不遮新行）与 `PAGE_VERDICT_REQUIRED`；到达门之上——`auth` 立即 FAIL（不置位的腿同样拦），置位腿（本批：Linux deb）曾要求 `healthy` 才落定；现改为 **`auth` 否决 + 截图内容见证**——
  外部 origin（dsh 的 http 页）上 Ryn 桥把 eval 回包 POST 到页面 origin（桥 `_ipcBase` 默认空串），
  打不到宿主 ⇒ 该页 DOM 探针恒超时（与预算无关，实测 15s×2/45s×2 同形）；故 deb 腿在截图后跑
  `smoke_capture_witness`：近空白（401 墙 mean≈1.00/sd≈0.04）与深色引导页（mean≈0.14）判失败，真 UI（mean≈0.81/sd≈0.13）通过。
- 截图时序（`smoke-install-linux.sh`）：裁决通过后再等 `SMOKE_REPAINT_SECONDS`（默认 3s，仅在有显示时）才拍，避开上一跳旧像素；睡后补一次进程探活留痕（不改 rc）。
- 落定窗预算（`package-linux.yml`）：窗口须覆盖「建窗残量 + 两跳导航(30+5)×2 + 探针 15×2」≈100s 的最坏链，amd64 90s→180s（arm64 仍 300s）。
- 范围：本批只开 Linux deb 腿；mac/win 腿不置位（到达即落定，`auth` 仍红），rpm 容器腿无 X（①不可达，恒②安装链，落定门不适用）。

## Alternatives considered

- **截图 OCR/像素断言**：落败——runner 无 OCR；像素断言随主题/缩放/字体漂移，脆。
- **只加"auth 必红"、保留到达即绿**：落败——`unknown`（探针失败/非同源/空文本）仍绿，假绿换皮；要的是显示腿"绿 = 同源 + 非空 + 未命中鉴权标记的一次采样"。
- **healthy 再加正向 UI 标记**（页面须含某个已知 UI 串）：落败——UI 文案随上游版本漂移，机器判据会随上游改字而假红；真 UI 归裁决后截图人眼，机器只证不变量。
- **冒烟侧直连 dsh HTTP**（`[host] dsh web =` 行的 token URL 用 curl 复现 303→cookie）：落败——看不到 WebView 的 cookie jar 与 DOM，WebView 里已渲染好 UI 时它会误红；只能作服务端补充信号，cookie 稳定性专项另立。
- **在冒烟脚本里自行解析 DOM**：落败——脚本无 WebView 通道，唯一的 DOM 读点就是应用自身的探针。
- **同批给 mac/win 开门**：落败——两腿真病灶未修时开门只是把红提前，放大面不换取信息；本批最小面先让 Linux 诚实。
- **保留 `Evaluate` 计数常量**：落败——重进次数由编排形状固定为 1，常量与三态枚举并存是第二处真相。

## Consequences

- 交付不变量（一次采样）：同源 + 可见文本非空 + 不含鉴权标记 ⇒ `healthy`，显示腿据此判绿；真 UI 仍只由裁决后截图人眼判（机器不判像素）。
- 401 与 `unknown` 在置位腿必红；deb 腿①不可达时仍走②安装链回退绿（`SMOKE_VERDICT=install-chain`，与 full-chain 可区分——此绿不含落定与裁决）；mac/win 未置位 → 到达即落定（`auth` 仍必红），残余假绿面在那两腿；rpm 容器腿恒②安装链，落定/裁决门不适用。
- 代价：探针 15s×2 与两跳超时吃掉落定窗（已按最坏链放到 180s）；同源但采到空文本（提交后尚未渲染出内容）也判 `unknown` → 红：宁可红一次，也不让"还没渲染"冒充绿。
- arm64 腿仍在提交回调缺失处失败（无到达即无裁决），本批不涉及其原生 hang 根因。

## Testing

- Core 单测：`Classify` 三态（同源/非同源/空文本/标记大小写/文本内含分隔符只切首个）；`ClassifyDetail` 明细（origin/长度/无采样）；`PageProbeSample.TrySplit` 残缺采样；`Separator` JS 字面量安全。
- Desktop 单测：`RynProbeValue` 桥真实形态（JSON 字符串/转义还原/裸值/JSON null）——夹具用桥形态，裸采样夹具照不出的外壳漂移由此上锁。
- 冒烟自测（`smoke-install-linux.sh --self-test`）：`settle-auth-fails`、`settle-verdict-healthy`、`settle-verdict-unknown-fails`、`settle-verdict-missing-fails`、`settle-verdict-relapse-fails`（healthy 后塌陷取最后一条）、`settle-verdict-recovery`、`settle-log-fallback-auth-fails`（裁决只在 host.log 的宿主腿形态）、`wait_url-verdict-healthy`、`wait_url-verdict-missing-fails`。
- 评审收口：两轮三审（R1/R2/R3）无遗留 Blocker；首轮 R2 B1（桥回报 JSON 外壳）与 R3 两条 Blocker 已修并复评，R1 3 / R2 5 / R3 6 条 Suggestion 全收口。
- dispatch：逐轮取 verdict + 尾日志 + 截图三件；结论与截图回填本节。
