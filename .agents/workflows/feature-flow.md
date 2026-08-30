# 功能开发流程（feature-flow）

> 非平凡功能/变更的主链路；琐碎修改走简化路径（实现+测试+提交）。

1. **定案**：方案讨论收敛 → 写 ADR；Alternatives 强制。同会话内即落地的，proposed→implemented 可折叠——直接以 implemented 格式落档（格式由 verify-adr-format 把关）。
2. **实现**：按 ADR 范围动 src/tests/scripts；越界想法记 TODO 不顺手做。
3. **测试**：`dotnet test` 全绿 0 警告；行为级变更必须配套回归/快照。`scripts/**` 变更跑其自带自测或冒烟（如 preflight 假资产冒烟、`package-*.sh --stage-only`）；`.github/workflows/**` 变更必须 dispatch 实跑验证——ci.yml 绿不代表打包流水线绿，表达式错误只有真 runner 能暴露。
4. **门禁**：三脚本 + build 全绿（pre-commit 会再拦一次）。**架构机械化门禁**（见 ADR [2026-08-30-architecture-mechanization](../notes/implemented/process/2026-08-30-architecture-mechanization.md)）：`verify-code-health.py`（F1–F4 尺寸）+ `verify-code-conventions.py`（D004/D005 契约）+ `dotnet test` 的 `ArchitectureTests`（A2/A4/A5 依赖方向；A1/A3/A6 为留评审项，不机器化）——**先拆再上**为强制：diff 不得把文件/方法**顶过阈值**；触碰已超阈值文件须**先拆到阈值内**（拆分是合并前置，本次变更应让文件缩小）。
5. **评审（三重审核执行契约）**：**触发**——改行为/安全面 / IPC 帧契约 / 发版链路 / 跨模块行为，或用户指定的批量事后审核。**纯结构重构（零行为变更，测试+门禁可证）走轻审或简化路径**，不占重三审。契约：
   - **范围 = 单元 diff**：一次只审一个逻辑单元（单个提交/相干子集），评审代理拿精确 `git diff <base>..<head>`（用 `scripts/change-scope.sh` 界定）；**禁止**对整仓/全文件做快照 + 手工 diff（杜绝 `.review_tmp` 式无界深挖）。
   - **父级预消化简报**：主会话先给每个代理一份紧凑清单（改了哪些文件、什么模式、风险点、3–5 条定向检查项 + 指定文件/行），而不是"加载技能 → 审一切"。
   - **串行 + 有界**：R1(dsh-find-simplifications) → R2(dsh-code-review) → R3(dsh-archive-agent-notes)**依次**，前一个收口才放下一个；每个代理设**轮次/工具调用上限**，到限未收口即中断，返回已有部分结论，绝不无限挂起。
   - **确定性报告契约**：每代理返回 `Blocker[]`/`Suggestion[]`，每条 `文件:行 + 一句证据`；空即"无发现"。主会话逐条采纳/拒绝（附证据），处理跨路冲突（某路修复可能使另一路发现失效），修复一次性收口为单个 `refactor(review)` 提交。
   - **行为保持的第一证据 = 测试 + 门禁**：`dotnet test` 绿 + 机械化门禁绿已兜底行为，评审只补**测试/门禁盖不住的语义/文档面**（R2 异常边界/编排序、R3 ADR 状态/链接/口径），不重复全量重验。
   - **大批量回溯审核用 `workflow`**：多提交/多面回溯用 workflow 阶段化 fan-out（有界 + phaseline），不用一次性背景子代理。
   - 评审代理**须额外按**根「[AGENTS.md](../AGENTS.md) 评审检查项（AI 兜底）」+ [coding-standards](../../docs/coding-standards.md) / [architecture-standards](../../docs/architecture-standards.md) 核对 D001–D003（async 尾缀/async void/空 catch）/ R1 组合根只装配 / R3 边界完备 / IPC 强类型 ID——上游技能为通用清单、不含本项目规则，这些「留评审」项须显式补审。
6. **提交**：逻辑单元分粒度提交，conventional commits 格式。
7. **推送 + 观察 CI**：main 推送触发 ci.yml，绿了才算完；涉及打包/workflow 链路的按步骤 3 加跑实跑验证。
8. **收尾（每个功能批次完成即执行，不积压到会话结束；前置门 = 触发了步骤 5 的批次必须评审通过且修复已收口[CI 绿]，未触发的走简化路径直接收尾）**：按 [session-close](session-close.md) 检查单过一遍本批次相关项（提交对账 / README 双语同步 / 交接条目 / 待办速览）；ADR 保持与 shipped 现实一致的 implemented 表述。**done = 通过全部验证**：ADR（需要时）→ 机械化门禁（尺寸/依赖/契约）→ 测试（含回归）→ 评审（需要时）→ CI 全绿。
