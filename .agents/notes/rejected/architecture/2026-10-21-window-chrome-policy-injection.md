# Agent Note: 窗口 chrome 几何端口注入化

Status: rejected — 抽象准入第 1 问不通过：端口零替身、无具体变更被它挡住（2026-10-21 批 1 三审 R1 车道驳回）

## Problem

窗口 chrome 三变量几何此前经 Core 的 `IWindowChromePolicy` 端口 + `WindowChromePolicy.Default` 共享默认实例取用；生产侧（`PageBridge/CaptionBar`）直接取静态默认实例，端口「无人注入」——它既不是可替换的接缝，也不是被依赖的契约，只是一层绕路（`ChromeInsets.Resolve` 被包了一次）。2026-10-20 的功能闭环与架构一致性审计把它判为「弱连接」（本地审计稿 `.plan/审计报告-功能闭环与架构审计-2026-10-20.md` §5.3；该稿未入库，入库后读者可按本段复述内容核对）。

2026-10-21 的批 1 三审中，R1 车道按本仓抽象准入判据（`docs/architecture-standards.md` R6 第 1 问：说得出具体变更 = 有，说不出 = 为模式而模式）判**不通过**：全仓该端口只有一个实现（无状态薄包装，直调 `ChromeInsets.Resolve`），src 与 tests 中**零个替身**，唯一生产消费方本就只消费 `ChromeInsets` 值对象。R2 车道独立复核补上一条：即便把端口接进容器，也没有判据证明消费方真的从容器取（把它换成 `new WindowChromePolicy()` 的旁路，三条判据仍全绿）——这套抽象连「接缝」都未被钉住。

结论：**端口与薄包装实现撤回**；几何判据仍留 Core（`ChromeInsets` 值对象 + `Resolve` 纯函数），渲染器直接取 Core 解出的值对象。

## Alternatives considered

- **保留端口并补齐「消费方从容器取」的断言**（落败）：补断言只能证明「接缝存在」，回答不了「有什么具体变更被它挡住」——本轮未找到这样的变更；为模式加机器看护是把成本固化。
- **把 `ChromeInsets.Resolve` 的实现搬进端口实现、让端口成为几何的唯一入口**（落败）：入口唯一化解决的是「几何散落」，而几何本就只有一处（Core 的纯函数），该形态不产生新的可替换点。
- **删端口与薄包装实现、渲染器直调 Core 纯函数**（采纳）：`CaptionBar` 取 `ChromeInsets.Resolve(...)` 的值对象。几何语义仍归 Core（ADR window-chrome-depatch-core-ports 的实质收益不变），端口的抽象成本（接口 + 实现 + 容器登记 + 用例构造参数 + 渲染器首参 + 20 处测试改写 + 接线回归用例）归零。

## Consequences

- 几何判据仍住 Core，渲染器不自持几何知识；母篇的实质收益不变，母篇的端口部分由同批 Erratum 记明撤回。
- 若将来出现**具体的**第二个几何消费者（如平台差异 insets、全屏态来源），再按准入判据立项端口——届时的「具体变更」就是它的准入依据。
- 本篇的落地形态是**撤回而非新增**：批 1 的暂存集删除已入库的 `IWindowChromePolicy`／`WindowChromePolicy` 两个类型、`CaptionBar` 的静态默认取用点与对应的 `ChromeInsetsTests` 同解用例；注入化的容器登记与用例构造参数从未入库（只存在于本批未提交的评审对象里）。本篇涉及的撤回不改任何门禁（批内对 `verify-compose-root.py` 的改动是自更新栈四端口的 C7 换名，与本篇无关）。

## Related

- [window-chrome-depatch-core-ports](../../implemented/simplification/2026-10-08-window-chrome-depatch-core-ports.md)——几何下沉 Core 的母篇（其端口部分已撤回）。
