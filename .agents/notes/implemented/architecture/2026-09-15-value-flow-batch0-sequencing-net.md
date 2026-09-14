# Agent Note: value-flow-batch0-sequencing-net（值流管线批次 0：依赖图与记序安全网）

Status: implemented

Review: FULL/2026-09-15/R1=ok R2=ok R3=ok
Review: FULL/2026-09-15/R1=ok R2=ok R3=ok
（重审计行 2026-09-15 第二次：CI format 门 FINALNEWLINE 三处修复并入批次提交后按 75429b9 整批重提——force-with-lease 改写未上 CI 的 c95b6ed；出向 diff 重过 verify-review-tier/机器门禁/pre-commit/pre-push 全绿，评审证据按「改写后重新审计」纪律重落，两行对应同一批三审结论。）

## Problem

[composition-root-value-flow-pipeline](../../proposed/architecture/2026-09-15-composition-root-value-flow-pipeline.md)（proposed，四批次主 ADR）批次 1/2 动主链前需要两件前置：穷举依赖图（字段归属/拆分线/端口形状）与记序安全网——时序契约（退出序/引导握手/后台服务启动序）当时只活在注释与源码语句序里，机器不可查。本笔记承载批次 0 的执行定案（main ADR 保持 proposed，批次 3 才迁）。

## Decision

1. **依赖图定稿**（实施计划 §5 回填）：实测 32 字段 / 15 段阶段链（修正立项估计 25/13，含 \`_supervisorCtsRef\` 非恒等别名）；字段→拆分线五分（托盘控制器/更新协调器/Infrastructure 引导服务/退出管道/留组合根）+ A 类配置归批次 3 IOptions；端口形状：引导 = Core 接口完成句柄（\`Task<bool> WaitSettledAsync(ct)\` 形状）、退出 = 有序步骤构造数据 + once-guard 管道单例。
2. **引导握手单点**：\`BootstrapSettleGate.WaitSettledAsync(settled, timeout, ct)（批次 1 起居 Core/Services）\` 零行为抽取——监督器门控与共享 home 横幅两处等待收敛到同一测试等待点（OCE→false / Timeout→放行降级 / settled null→立即放行），\`timeout ?? Timeout.InfiniteTimeSpan\` 单点化两形态。
3. **记序测试三网**：\`BootstrapSettleGateTests\`（5 用例：null/已置位/置位前拦截/取消/超时降级）；\`CompositionRootSequenceTests\`（Run 主链 15 段源序 + TCS 创建先于门控接线，带实参调用串天然唯一、无参串以分号锚定）；退出全序复用既有 \`ExitPipelineTests\`（批次 1 更名）（cancel→stop→release→dispose→close→watchdog 已覆盖，不新建）。
4. **评审档案**：FULL 三审（R1/R2/R3）0 Blocker；5 Suggestion 收口（末尾换行×2、注释去重、门分支折叠、源序锚定）+ 1 延后（测试 RepoRoot 与 ArchitectureTests 去重——需改已冻结评审面，批次 3 收口时处理）+ 1 拒绝（SetWhileWaiting 50ms 断言：R2/R3 双核为确定性契约断言，非空转）。

## Consequences

- \`dotnet build -warnaserror\` 0 警告；\`dotnet test\` 621/621（基线 614 + 新增 7）；机械化门禁（adr-format/md-links/code-health/code-conventions/handoff/governance/cookbook）全绿。
- 零行为变更自证：无 env 注入 / 无公共 API 签名变化（新增类型为新增面）/ 无 spawn 形态变化 / 无可观察副作用变化；等待逻辑 await 序与分支逐一等价（R2 逐分支核毕）。
- 测试数 614→621：README 双语徽章/testing.md 基线行暂维持 614 旧值（双件互相一致），覆盖率跟值随 CI cobertura 跟值批统一刷新。
- 测试 RepoRoot 与 ArchitectureTests 逐字重复留待批次 3 收口收敛（需改不在本批评审面的既有文件）。

## Alternatives considered

- **不抽取门、直接测组合根私有闭包**：门控闭包深嵌 SetupSupervisor 的 Task.Run + RuntimeSupervisor，行为测试需跑 Ryn 应用（GTK 主循环）——不可测即无网。落败；零行为静态抽取是唯一可测路径。
- **批次 0 一并建并发/竞态网**：计划 §0 不变量表将 exactly-once/竞态归宿标 0/2，且并发网价值依赖批次 2 的值流形态（现在建会在批次 2 重写）——推迟，批次 2 随值流管线补。
- **不设批次级 ADR、Review 证据挂主 ADR**：verify-review-tier 要求变更集内 **implemented** ADR 携带新增 Review 行，主 ADR 批次 3 前必须保持 proposed；沿用 clean-architecture B1–B4「伞 ADR + 每批卷」先例，批次级 implemented 卷承载证据。