# Agent Note: 冒烟判门收紧——companion 装配判红与 ②回退门砍除

Status: implemented

Review: FULL/2026-10-25#2/R1=ok R2=ok R3=ok; hotfix5 LIGHT/2026-10-25#5/R2=ok（判门竞速修复，用户指令全修）

## Problem

安装冒烟存在两类「探针未达标却收绿」：其一，**②回退门**（timeout_fallback 及等待循环的两处绿出口）——① dsh web 未出现但②引导活着（或超时仍只有②）即收绿，为 CI 受限环境留的保底口子；其二，**companion 装配仅观测不判红**——CI 上 companion 装配失败而冒烟恒绿（实测 run `40127406189` linux/win、Ryn 同败 run `40124270810`；前置缺失自 8-31「简单壳」未变，更早 run 未回溯【推断 · 未证】），装配失败行、异常跳过行（spec 解析失败/资源未携带）均不进判定面。用户拍板（2026-10-25）：探针探活的都如实红，假绿即假信号。

## Decision

- **②回退门砍除**：`timeout_fallback` 删除；`smoke_wait_ready` 循环内「进程退出+只见②→收绿」与循环尾「超时+只见②→收绿」两处出口改红。②行保留为观测信号（「已见②安装链，继续等①」的心跳语义不变）——它证明安装链活过，不再充当绿依据。
- **companion 判门分两层**（run `40140846103` 四腿红实证的竞速修复；唯一家在 smoke-wait-lib）：
  - failfast 层（`smoke_wait_ready` 每轮查）：装配失败行（含引导/非引导两路径的措辞变体）与异常跳过行（spec 解析失败/无可用来源）→ 红——行出现即终态失败信号；
  - terminal 层（只在①落定后/进程退出/等待窗尽调）：「未就位，加入待装清单」后未见「经事务管线换入 active」→ 红——装配虽在 spawn 前收口，但收口可耗时（`npm install -g pnpm` 几十秒），在途窗口 PENDING&&!ACTIVE 恒真；原实现三查合一每轮跑，实测 58ms 即误红杀树（npm install 被 SIGTERM exit=143——Windows 腿失败行的来历）；
  - 已就位（无需安装）或换入 active → 过。
- mac x64-on-Rosetta 跳过腿不经判门（执行层已跳过，exit 0 在前）；rpm 容器腿经共享库自动继承。
- 自测夹具：companion 判门四态矩阵 + ②回退门砍除回归（超时/退出只见②必红）。

## Alternatives considered

- **三查合一每轮判门（不分层）**：初版论证「装配先于 spawn 收口→等待循环开始时状态已定」被 run `40140846103` 推翻——收口本身耗时（npm install 在途几十秒），在途窗口即误红。弃，分 failfast/terminal 两层；terminal 层即「终态在位断言」。
- **产品面同步阻断启动**：缺插件不该让桌面打不开（比缺伴生功能更糟）；判定面收紧与产品面 best-effort 分离。弃。
- **②回退门降级为 warning 保留绿**：假绿就是假绿。弃。

## Consequences

- 「①死但引导活」形态从绿变红（如实）；受 CI 环境限制导致①出不来的腿将以红暴露真问题，不再有保底口子。
- companion 缺失/装配中断/静默跳过即刻红；pnpm 前置修复后 CI 应见 companion 首装成功（换入 active 行 + 判门过）。
- 冒烟绿的标准收紧为：装上 → companion 在位 → dsh 真起来 → 铸币/存活/像素全达标。
