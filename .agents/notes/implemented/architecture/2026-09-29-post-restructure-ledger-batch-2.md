# Agent Note: post-restructure-ledger-batch-2（挂账清账批 2——公共面收窄/Erratum 清理/托盘通知钉）

Status: implemented

Review: LIGHT/2026-09-29/R2=ok R3=ok（R2 首轮 0 Blocker，2 Suggestion 采纳折入；R3 首轮 1 Blocker——新 ADR 自指已删路径自造第 8 条 warning，验轮闭合）

## Problem

清账批（2026-09-29-post-restructure-ledger-batch）评审与前期批次再留三笔挂账，账面与代码/笔记/测试现状不同步：

1. **LauncherActivation 剩余公共面宽于消费面**：`ShowCommand`/`AckResponse` 消费仅剩同类文件内
   （`PrimaryListener`）且测试未引用；`TryBindPrimary`/`NotifyPrimary`/`PrimaryListener` 生产消费仅
   app 程序集（IIV 已授予）——清账批 R1 评审产出。
2. **`--facts` 存量 warning 待清理**：implemented 笔记正文的仓内路径 token 因 Services 伞目录退役、
   脚本删除、测试归位而零命中（E 批挂账；彼时点记 29，本批开工时实测 23——n=1 本机工作树实测：扫描器 corpus 含未入 git 的
   本地工作文件，纯 commit 树复测基数不同，数值不可跨树复现）。
3. **托盘「检查更新」通知路径无回归网**：`DesktopTrayCommandRouterTests` 的 `MakeRouter` 有
   `machine` 形参但零测试传入、`notify` 未接——`CheckUpdateInBackgroundAsync` 的就绪态通知分支
   不可达（F 批 R2 挂账）。

## Decision

三件随一个 LIGHT 批清账（R2+R3 双路评审，R1 免——三面互不重叠且无 FULL 触发器）：

1. **LauncherActivation 五成员降 internal**：`ShowCommand`/`AckResponse`/`TryBindPrimary`/
   `NotifyPrimary`/`PrimaryListener`。「协议常量保持固定」约束的是值而非可见性——同批先行改写
   post-restructure-ledger-batch 的 Consequences 判词，外部契约仅剩 `ResolveInstanceSocketPath`
   与类本身。测试与 app 程序集经既有 IIV 零改动。
2. **`--facts` 存量逐条处置**（三类）：**搬迁类修引用** 13 篇 20 处 token 指向现行落点（Services
   伞目录退役→Runtime/PageBridge/Core/Platform 现址；`ArchitectureTests.cs` 归位；上游仓路径加
   `deepseek-harness/` 前缀限定；coverage 示例以现行真实文件重写）；**删除类补 Erratum** 3 篇
   （bundle-runtime* 脚本×1 篇、理念沉淀.md 早期工作文档、ExternalLinkClickCatcher.cs——拦截已迁
   companion；三者均不带反引号引用，避免为已删路径自造新 warning）；**已记录类不动** 3 篇（artifact-verification-chain 正文自述退役、gui-freeze-probe
   既有 Erratum、dshmarket Related 已叙退役）。warning 23→7：余 7 条均为删除类且各有记录，
   advisory 长明属预期——扫描器对历史叙述不豁免，「正文即记录」优先于清零数字。终态 7 条以
   本批提交的工作树为准（同上测量条件）。
3. **托盘通知夹具**：`MakeRouter` 接 `notify`；新增最小真实状态机构造（`StubPersistence` 空桩 +
   check 命中/未命中两形态），钉住两分支——机器达 `Ready` 后通知送达（标题=TrayCheckFeedback.Title、
   文案含版本号）与 `UpToDate` 结束态通知「已是最新版本」；受理帧 `{}` 先行返回、通知异步不阻塞
   路由的契约由 `TaskCompletionSource` + 5s 超时钉住。

## Alternatives considered

- **三件拆三批**：单件 diff 更窄，但三次 ADR/冻结/评审启动的流程开销远超 LIGHT 档合并成本；三面
  互不重叠、无相互失效风险。落败。
- **改 `verify-adr-format.py --facts` 跳过带 Erratum 的笔记**：编辑 verify-*.py 即 FULL 触发且属
  门禁判据改动须自证；「正文即记录」原则下历史引用保留反引号有查证价值，扫描器误报不足以动门禁。
  落败。
- **托盘通知用 mock 状态机**：仓库约定 mock 只用于昂贵/非确定性边界——`UpdateStateMachine` 是纯
  逻辑委托注入可真实构造（对齐 UpdateStateMachineTests 先例），真实状态机顺带覆盖 `CheckAsync`
  全链与真实状态转移；`StubPersistence` 是 IO 边界桩，属合理 fake。落败。

## Consequences

- `LauncherActivation` 公共面 -5：外部契约仅剩 `ResolveInstanceSocketPath` 与类本身。
- `--facts` 23→7：余 7 条（6 篇删除类）各携带记录，后续新增 warning 一律可视为 actionable。
- 托盘「检查更新」结束态通知两分支有回归网；`MakeRouter` 的 `notify` 缺省 null 不影响既有 9 测。
- 基线 884→886；测试零外部行为变化（纯可见性收窄 + 测试夹具）。

## Risks

- 就绪态通知测试依赖 `Task.Run` 时序：`TaskCompletionSource`（RunContinuationsAsynchronously）+
  5s 超时兜底，CI 慢机最坏超时失败不悬挂。
- Ready 文案断言含版本号：文案措辞变更会破钉——属预期（文案契约变更须同步改钉）。
