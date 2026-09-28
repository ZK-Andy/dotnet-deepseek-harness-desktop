# Agent Note: macOS 401 墙 grace 重载与像素见证门

Status: implemented

Erratum: 2026-09-28 — 编排落点换家：`DesktopBootstrap.WebAuth.SetleWebSessionAsync` 随组合根形态分离迁出（ADR architecture/2026-09-28-compose-root-form-separation），现为 `Bootstrap/StartupSequence.WebSession.cs` 的 `StartupSequence.SettleWebSessionAsync`；本篇描述的 grace 重载链路已按 ADR page-verdict-gate 退役，仅余重铸+重载一次的自愈链。

Erratum: 2026-09-28 — 正文「`package-macos.yml` 加 `brew install imagemagick`（见证执行面）」的落点已换家：合并为 `package.yml` 的 `build-macos` job。正文不动。
Erratum: 2026-09-28 — 落点换家：正文所指 `scripts/smoke-settle-lib.sh` 已随「脚本层收口」拆并——等待与落定面迁至 `scripts/lib/smoke-wait-lib.sh`，判定/证据面（结论行/存活门/像素见证/证据打印）迁至 `scripts/lib/smoke-verdict-lib.sh`；同批三平台入口与 rpm 容器腿改为 source 共享库并拆出 `scripts/smoke-linux-rpm-inner.sh`（见 [script-layer-consolidation](../process/2026-09-28-script-layer-consolidation.md)）。正文不动。

Review: FULL/2026-09-27/R1=ok R2=ok R3=ok

Related: 前序 `bug-fix/2026-09-14-bootstrap-cross-scheme-cookie-401`（两跳）+ `bug-fix/2026-09-26-webauth-token-reentry`（P0 自愈）+ `testing/2026-09-26-page-verdict-gate`（裁决门）+ `testing/2026-09-26-smoke-witness-real-and-eval-first-hop`（见证变实）+ 诊断 dispatch run `36259835977` + 首版 tag run `36215776049`/重发 run `36221929541`（mac 四张截图）。

## Problem

mac 双腿"到达绿 + 401 像素"的空心绿是确定性产品 bug，不是 flaky：

1. **三张成功截图全是同一面 401 墙**（run1 x64/arm64 + run2 arm64，`dsh web authentication required…`），verdict 全是到达门判的 full-chain，像素没人看。
2. **诊断 dispatch `36259835977` 定界**：arm64 落定 2s/6 到达后，探针 `15s×2` 超时 → `页面裁决=unknown（探针无采样）` → 跳过自愈检查。外部 http 页上 eval 结果回包到不了宿主（Ryn 通道机制 + `smoke-settle-lib.sh` 既有结论），故 P0 自愈在真实 401 路径上是死代码——它只在"采样到 auth 文本"时开火，http 页永不采样，永远没机会修截图里那面墙。
3. **dsh 铸币链自证完好**（本地 curl 实测）：裸 `/` → 401；`/?token` → 303 + `Set-Cookie(HttpOnly; SameSite=Strict)`；凭 jar 再访 `/` → 200 + 34KB 真 UI。缺陷在 WKWebView 客户端存/发 cookie，不在服务端。
4. **x64 run2 型桥死另案**：同代码 run1 全过，run2 eval 与导航调用全超时、卡自家引导页——病 runner 抖动（2/3 跑），单记不拦主线，不冻结（冻结会埋掉真机用户的墙）。

## Decision

- grace 重载（产品面，`DesktopBootstrap.WebAuth.SetleWebSessionAsync`，macOS 域内，有界 1 次）：终态非 healthy → loud 留 verdict → 宽限 `AuthGraceReloadDelaySeconds`（默认 8s，给 cookie 落盘）→ 无 token 重载 `AuthorityRoot`（纯 cookie 检验，不重铸；铸币只在 token 跳发生）→ 再探针再裁决。退出取消照常上抛；healthy 直接返；非 macOS 直接返（其余平台零行为变更）。
- 新键进 `RuntimeTimeouts` + `appsettings.json`（可调参数不硬编码；默认 8），单测跟默认/覆盖值。
- mac 像素见证门：`smoke_capture_witness` 搬进 `smoke-settle-lib.sh` 共用（Linux 沿用默认裁剪几何，阈值与自测夹具一字不改）；mac 到达后 repaint 3s + 中央裁剪见证（与 Linux 同阈值，避菜单栏/Dock chrome），401 墙/引导页/截图缺失即红。
- 截图等 verdict 行数静默（事件驱动：出现后 N 秒行数不变即拍，预算 150s 有界；恒返 0，只定截图时机，不判门）——应用侧 1 条还是 2 条 verdict（grace 重载）都对齐终页，不靠固定睡眠猜。
- D2 诊断段转正：verdict/重进/探针行回显保留为永久观测（token 脱敏，只读不判门）；`wait_verdict` 改静默语义。
- `package-macos.yml` 加 `brew install imagemagick`（见证执行面）。

## Alternatives considered

- **继续加落定窗/探针预算**：落败——病灶在 cookie 存发与探针盲区，与等待时长无关（诊断 unknown verdict 即证；Linux 同类教训在前）。
- **x64 照 arm64-linux 冻结**：落败——同代码 run1 全过，属环境抖动；且冻结把真机 401 埋进绿 CI（本批用户明确否决）。
- **Ryn cookie 注入 API / 上游修 saucer store**：落败——Ryn/saucer 层无 cookie 注入面；per-uuid store 属引擎行为，修复周期不可控；壳侧 grace 是零依赖止血。
- **env 门控只做实验不进产品**：落败——真机 mac 用户同样撞墙，修必须进产品语义；域内限定（macOS）已是最小 blast（Linux 见证门已绿、win 腿未到 settle，不碰）。
- **首窗直给 token URL**：落败——前序 ADR 已否（建窗时 dsh 不存在，要动启动编排）。
- **OCR/像素断言替代 mean/sd**：落败——沿用既有阈值口径（runner 无 OCR、断言随主题漂移），中央裁剪只避 chrome，不改判据。

## Consequences

- mac 到达绿必须配真 UI 像素：401 墙即红；grace 治愈则 verdict/像素双绿放行。
- Linux：witness 调用点搬家零语义差（自测全过）；grace 逻辑 macOS 域内，Linux/Win 零行为变更（`dotnet test` 812/812 计数与基线一致）。
- 代价：mac settle 成功路径增加 verdict 静默等待（有界 150s）+ 可能的 grace 8s + 一次重载再探针；均为 loud 留痕。
- P0 自愈的 http 页盲区如实记录：本批不动其触发语义（采样到 auth 仍重进一次），未知采样仍放过启动；盲区根治（探针通道）另立项。

## Testing

- `RuntimeTimeouts` 新键默认/覆盖单测（8/29）；`dotnet build` 0 警告；`dotnet test` 812/812。
- `smoke-install-macos.sh --self-test` 全绿（含 verdict 静默四例 + 中央裁剪见证明暗两例，convert 真跑）；`smoke-install-linux.sh --self-test` 全绿（搬家回归）。
- dispatch `package-macos` 看 verdict/见证行与像素定 (a)/(b)；`package-linux` 看 amd64 见证仍过（grace 零波及实证）。
