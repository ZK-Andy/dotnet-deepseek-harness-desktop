# Agent Note: 冷启动表面输入关联修复——首映射引导尾步撤回重映射

Status: implemented

Review: LIGHT/2026-10-28#1/R2=ok（0B/2S 全采纳折入本批）

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

2026-10-28 真机取证（v0.7.8 装机冷启动，用户配合多轮实验；工具面：X 层 C 探针 + 探针采样）：

- **实测**：`0ef785a` 首映射复验按设计收口（激活请求首发，300/800/1500ms 复验零补发零直落——键盘焦点本体确在本窗子树），而键盘全死、页面点击「穿透」到后面的 Wayland 原生窗（ZCode）。用户 7 轮点击探针实锤：每次点击 X 层活动窗+焦点本体都正确切到壳窗（最长驻留 10s），输入仍不进。
- **实测（排除面）**：X 层抓取探测（XGrabPointer/XGrabKeyboard 双 Success＝无客户端持有抓取）、XShape 输入区（bounding/clip 未设）、窗口几何/堆叠/WM_STATE 全部正常；全屏 O-R InputOnly 窗 unmap 后依旧失焦（非拦截者）。
- **实测（治愈通道）**：对壳窗做 X 层 `XWithdrawWindow`+`XMapWindow` 重映射——输入即刻复活，焦点本体落回本窗（n=1【探索性】，与 0.7.0 以来「托盘唤回自愈」历史记录同机制）。
- **定性（机制推断标注【推断 · 未证】）**：冷启动竞速下壳窗在 Mutter↔Xwayland 场景的表面输入关联损坏——X 协议层一切健康而 Wayland 侧输入路由不达；重映射强制 WM 重新管理窗口、重建表面关联。损坏的触发时点与竞速窗口未捕获（0.7.3/0.7.7/0.7.8 间歇发作，探针未跑到迁移时刻）。

## Decision

**首映射引导序列尾步补确定性撤回重映射**（`X11FocusBootstrap`，连接/once/错误处理器豁免面均不变）：

- 复验循环（300/800/1500ms）不再提前 return——任一时点收口只置位跳出循环，序列尾必达尾步。
- **`RunSurfaceRemap`**：读撤回前 `_NET_WM_STATE`（`DecideRemapRestore` 纯函数：VERT+HORZ 双原子同在才按最大化恢复，与 Avalonia maximized==2 同判据）→ `XWithdrawWindow`（XDefaultScreen）→ 400ms 间隔（实测 500ms 生效，取整）→ `XMapWindow` → 最大化恢复（`_NET_WM_STATE` ADD ClientMessage，l[3]=source indication=1）→ 重发 `_NET_ACTIVE_WINDOW` 激活 → 400ms 后单次焦点本体留痕复验（到位/未跟随二态日志，不再触发恢复动作）。
- 全序列仍 bounded（复验 ≈2.6s + 重映射 ≈0.8s，gap 与留痕复验各 0.4s），此后不再干预的既有边界不变；`SendActivate` 底座泛化为 `SendRootClientMessage`（四 data 槽，激活与态恢复共用）。

**为什么重映射是正确层**：X 层修复面（激活/直落/复验）全部健康依旧失焦＝故障不在 X 焦点协议层；表面关联的重建通道里，壳侧可发起的只有「让 WM 重新管理窗口」这一条（withdraw+map），X 协议对已管理窗由任意连接发起均合法。

## Alternatives considered

- **条件重映射（先检测表面损坏再修）**：无检测信号——X 层全部指标健康（本批排除面即证明），Wayland 侧表面状态从 X 连接不可观测（Shell Eval 被 unsafe-mode 禁用）。落败。
- **Avalonia 层 Hide()/Show()**：同型 X 层效果但耦合 Avalonia 内部可见性状态（OnOpened 重入、`_chromeWired` 接线守卫、托盘唤回路径交叉）；X 层 withdraw+map 即本批实证的治愈操作原样，不引入状态耦合面。落败。
- **焦点丢失轮询兜底（常驻低频 watchdog）**：常驻轮询治症状且永不关账；本批为确定性一次性重建。落败（用户当场否决过的方向）。
- **只赌更晚的复验时点（如 reload 后再加 checkpoint）**：时点赌注，复发窗口不可枚举。落败。

## Consequences

- 买到的：冷启动表面关联损坏获得确定性修复通道，不再依赖用户手点托盘唤回；`0ef785a` 的复验/直落机制保留在位（背离态兜底语义不变，仅收口不再提前终止序列）。
- 付出的：①重映射瞬间窗口从屏幕消失 ~0.4s 再回来（冷启动 holder 页期，视觉上一次闪动）且丢失最大化态（按撤回前状态恢复，半贴靠不误扩）；②冷启动后 ~3.4s 内若用户已刻意把焦点交给别的窗，尾步会把焦点拉回壳窗（窄窗口可接受，ADR 记账）；③损坏的根因（Mutter↔Xwayland 竞速细节）仍未知——本批是确定性缓解不是根因消除，若上游修复可撤尾步。
- 行为契约豁免沿用先例：`Run` 总 `try` 包整个 X11 交互序列，`RunSurfaceRemap`/`ReadHasAtom` 在同一豁免面内；空 catch 命名沿用既有注释。
- 测试：`DecideRemapRestore` 双原子判据单测（4 形态）折入既有决策面测试文件；X11 I/O 面维持 best-effort 不单测先例。
- 真机验证项：下一次装后冷启动实测键盘面；日志可辨（「撤回重映射重建窗口表面」+「重映射完成，焦点本体已到位/未跟随」）。

## Related

- 先例 ADR：`2026-10-28-first-map-input-focus-recheck`（复验本体化+直落；本批真机验证其「按设计工作但未治愈」的回执）、`2026-10-25-exit-kill-focus-debounce-batch`（焦点兜底原设计、WM_TAKE_FOCUS 证伪）。
- 取证现场：X 层探针源码在真机 /tmp（wintree/grabprobe/shapetoo/unmaptest/remap），journal 与 host.log 2026-10-28 03:25 起。
