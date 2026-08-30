# 功能开发流程（feature-flow）

> 非平凡功能/变更的主链路；琐碎修改走简化路径（实现+测试+提交）。

1. **定案**：方案讨论收敛 → 写 ADR；Alternatives 强制。同会话内即落地的，proposed→implemented 可折叠——直接以 implemented 格式落档（格式由 verify-adr-format 把关）。
2. **实现**：按 ADR 范围动 src/tests/scripts；越界想法记 TODO 不顺手做。
3. **测试**：`dotnet test` 全绿 0 警告；行为级变更必须配套回归/快照。`scripts/**` 变更跑其自带自测或冒烟（如 preflight 假资产冒烟、`package-*.sh --stage-only`）；`.github/workflows/**` 变更必须 dispatch 实跑验证——ci.yml 绿不代表打包流水线绿，表达式错误只有真 runner 能暴露。
4. **门禁**：三脚本 + build 全绿（pre-commit 会再拦一次）。**架构机械化门禁**（见 ADR [2026-08-30-architecture-mechanization](../notes/proposed/process/2026-08-30-architecture-mechanization.md)）：`verify-code-health.py`（F1–F4 尺寸）+ `verify-code-conventions.py`（D004/D005 契约）+ `dotnet test` 的 `ArchitectureTests`（A1–A5 依赖方向）——**先拆再上**为强制：diff 不得把文件/方法**顶过阈值**；触碰已超阈值文件须**先拆到阈值内**（拆分是合并前置，本次变更应让文件缩小）。
5. **评审**：三重审核以**子代理逐个执行（不并发）**——同一提交范围，三个子代理分别按 dsh-find-simplifications / dsh-code-review / dsh-archive-agent-notes 视角**串行**独立审查（各自带全量技能指令与仓库约定，不共享中间态；**前一个收尾拿到结论后再放下一个**，避免并发子代理互相卡死）；主会话汇总裁定（逐条采纳/拒绝并附证据），复核发现间冲突（某路的修复可能使另一路发现失效），修复一次性收口为单个 `refactor(review)` 提交。触发条件（任一）：重大变更——触及安全面 / IPC 帧契约 / 发版链路 / 跨模块行为；或用户指令的批量形态——对指定提交范围做事后审核。**评审代理在跑上游技能之外，须额外按根「[AGENTS.md](../AGENTS.md) 评审检查项（AI 兜底）」+ [coding-standards](../../docs/coding-standards.md) / [architecture-standards](../../docs/architecture-standards.md) 核对 D001–D003（async 尾缀/async void/空 catch）/ R1 组合根只装配 / R3 边界完备 / IPC 强类型 ID——上游 dsh-code-review 技能为通用清单、不含本项目规则，这些「留评审」项须显式补审，否则兜底落空。
6. **提交**：逻辑单元分粒度提交，conventional commits 格式。
7. **推送 + 观察 CI**：main 推送触发 ci.yml，绿了才算完；涉及打包/workflow 链路的按步骤 3 加跑实跑验证。
8. **收尾（每个功能批次完成即执行，不积压到会话结束；前置门 = 触发了步骤 5 的批次必须评审通过且修复已收口[CI 绿]，未触发的走简化路径直接收尾）**：按 [session-close](session-close.md) 检查单过一遍本批次相关项（提交对账 / README 双语同步 / 交接条目 / 待办速览）；ADR 保持与 shipped 现实一致的 implemented 表述。**done = 通过全部验证**：ADR（需要时）→ 机械化门禁（尺寸/依赖/契约）→ 测试（含回归）→ 评审（需要时）→ CI 全绿。
