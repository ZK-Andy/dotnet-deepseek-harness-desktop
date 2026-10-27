# Agent Note: 首映射焦点兜底复验升级——本体指标 XGetInputFocus + 直落 XSetInputFocus

Status: implemented

Review: LIGHT/2026-10-28#1/R2=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

2026-10-28 活体证据（0.7.7，v0.7.5 焦点兜底在位）：

- **实测（用户报障 + 当场取证）**：两次拉起 0.7.7，用户实测首映射后键盘面（媒体键/页内 tab/Alt-Tab 的既有症状群）不可用；日志中 `焦点兜底：已发 _NET_ACTIVE_WINDOW 激活请求` 首发，300/800/1500ms 复验**零补发**——即复验读到的 `_NET_ACTIVE_WINDOW` 已是本窗，代理指标判定成功。
- **实测（健康态对照，同日同拉起方式）**：`XGetInputFocus` == `_NET_ACTIVE_WINDOW` == 本窗，三值一致；且 10-25 真机验收（v0.7.5）同样三值一致态下键盘可用。
- **推断（标注：推演，未捕获故障态本体值）**：故障态下 `_NET_ACTIVE_WINDOW` 已指向本窗而 X input focus 未跟随——现有复验读的是 EWMH 代理指标而非键盘焦点的本体，两值背离时代理指标放行，复验形同虚设。故障态本体值的直接取证未取得（故障为间歇态，两次复测均未复现健康态以外的样本）；ADR「付出的」节本就预留了该残口：「Mutter 焦点窃取防护极严时可能仍吞激活（复验三连发缓解，真机验证项）」——本批即该验证项的失效回执。
- 另实测：mutter 侧幽灵活动窗（无 WM_CLASS 死窗）贯穿应用整个生命周期存在（启动前/关闭后活动窗均落回同一死窗 id），是焦点异常的伴生环境态，本批不修。

## Decision

**复验升级为本体指标**（`X11FocusBootstrap`，进程级 once/时点表/连接模式均不变）：每个复验时点同时读 `_NET_ACTIVE_WINDOW`（EWMH 活动窗）与 `XGetInputFocus`（X input focus，键盘焦点本体），按纯函数 `Decide(self, ewmhActive, inputFocus, inputFocusInSubtree)` 定动作：

- `inputFocus == 本窗` 或 input focus 落在**本窗子树**（`XQueryTree` 沿父链上行判后代，深度有界；查询失败按非后代处理——直落主修方向优先于理论误伤面）→ 成功收口。后代持焦是用户合法态（复验窗内点击 WebView 子窗），直落顶窗反而从页面抢走键盘（R2 S2 采纳）。
- `inputFocus != 本窗/后代` 且 `ewmhActive != 本窗` → 维持既有路径：补发 `_NET_ACTIVE_WINDOW` 激活请求。
- `inputFocus != 本窗/后代` 且 `ewmhActive == 本窗` → **新增直落分支**：`XSetInputFocus(本窗, RevertToPointerRoot, CurrentTime)` + `XFlush`，日志带复验时点留痕。复验窗外不再干预的边界不变。

依据（源码级）：`WM_TAKE_FOCUS` 应答路线已被证伪（见 Alternatives）；对已映射可见窗，XSetInputFocus 由任意连接发起均合法（X 协议无建窗客户端限制——与 Mutter 发 `WM_TAKE_FOCUS` 用 `event_mask=0` 的投递限制是两条不同协议路径），直落不经过 WM 焦点窃取防护的判定面。`RevertToPointerRoot` 与 Mutter 自身 `XSetInputFocus` 的 revert 形态一致（健康态实测 revert=1）。

**X 错误面防放大**：本连接安装线程级 `XSetErrorHandler`（返回 0 吞错——默认 Xlib 处理器会终止整个进程；安装位于 Run 的 try 内，失败随整体放弃并走 finally 收链；delegate 存静态字段防 GC）。直落与父链上行可能抛 BadMatch/BadWindow（复验窗内用户最小化/窗口销毁等），吞掉后由下一次复验往返自然消费，不放大为壳崩溃；`XSetErrorHandler` 封送按 C 原型对型为指针返回（R2 S1 采纳）。

## Alternatives considered

- **往 `WM_PROTOCOLS` 加 `WM_TAKE_FOCUS` 并应答**：源码级证伪（先例 ADR `2026-10-25-exit-kill-focus-debounce-batch` Alternatives 节）——Mutter 以 `event_mask=0` 发送该消息只投递建窗客户端（Avalonia 连接），本壳独立连接收不到；Avalonia 12.1.4 `X11Window` 也不处理该消息；只加原子不应答会让 Mutter 放弃直落焦点改等应答，反而恶化。维持落败，本批不改道。
- **写 `_NET_WM_USER_TIME` 真时间戳安抚焦点窃取防护**：EWMH 要求时间戳来自真实用户事件，独立连接无事件源，取真实时间戳需 PropertyChangeMask 事件往返（选掩码与 Avalonia 既有选择面的冲突面未证）；且本次故障态根因未证实是窃取防护时间戳判定（故障态本体值未捕获），先修本体指标这一确定面。落败（archived：若直落分支实测仍救不回，重启该路线与故障态取证一并立项）。
- **扩大复验窗（更多时点/更长时长）**：治标不改指标——背离态下复验再多次仍是代理放行；且总时长有界是既有边界。落败。

## Consequences

- 买到的：复验从「代理指标一致即放行」变为「键盘焦点本体到位才收口」；EWMH 背离态获得有界直落兜底；决策面抽纯函数，三态可单测（X11 I/O 面维持 best-effort 不单测先例）。
- 付出的：直落 XSetInputFocus 与 Mutter 焦点模型存在竞速可能（Mutter 后续焦点变更事件可能改写，用户随即点击其他窗属合法覆盖）；若故障层不在 X input focus（如 GTK/页内焦点态），直落分支救不回——此时日志会留下「已直落仍失焦」的观测点，转 GTK/页内焦点取证另行立项；故障态本体值仍未捕获，本批「背离为故障机制」属推演标注（见 Problem）。
- 行为契约豁免沿用先例：`X11FocusBootstrap.Run` 的总 `try` 包整个 X11 交互序列（粗粒度 best-effort 边界，任一步失败整体放弃并留痕）——本批新增读写仍在同一豁免面内。
- 测试：`Decide` 决策面单测（本体到位 / 代理未中走激活补发 / 代理已中走直落 / 后代持焦即收口；含 None/PointerRoot/幽灵窗输入形态）。X11 I/O 面维持 best-effort 不单测先例。
- 评审收口（LIGHT R2/2026-10-28#1，0 Blocker / 4 Suggestion 全采纳已折入本批）：S1 `XSetErrorHandler` 返回型对型为指针；S2 后代持焦视为达成（子树判定 + 失败按非后代）；S3 直落日志带复验时点；S4 handler 安装移入 try。
- 真机验证项：下一次装后重启/冷启动实测键盘面；若直落分支触发，日志可辨（含时点的留痕行）。

## Related

- 先例 ADR：`2026-10-25-exit-kill-focus-debounce-batch`（焦点兜底原设计、`WM_TAKE_FOCUS` 证伪、硬退终态）。
