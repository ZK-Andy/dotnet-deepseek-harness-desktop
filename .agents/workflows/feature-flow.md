# 功能开发流程（feature-flow）

> 非平凡功能/变更的主链路；琐碎修改走简化路径（实现+测试+提交）。

1. **定案**：方案讨论收敛 → 写 ADR；Alternatives 强制。同会话内即落地的，proposed→implemented 可折叠——直接以 implemented 格式落档（格式由 verify-adr-format 把关）。
2. **实现**：按 ADR 范围动 src/tests/scripts；越界想法记 TODO 不顺手做。
3. **测试**：`dotnet test` 全绿 0 警告；行为级变更必须配套回归/快照。`scripts/**` 变更跑其自带自测（如 `check-pin-freshness.sh --self-test`、preflight 假资产冒烟）；`.github/workflows/**` 变更必须 dispatch 实跑验证——ci.yml 绿不代表打包流水线绿，表达式错误只有真 runner 能暴露。
4. **门禁**：三脚本 + build 全绿（pre-commit 会再拦一次）。
5. **评审**：三重审核 dsh-find-simplifications → dsh-code-review → dsh-archive-agent-notes，逐轮收口、收口独立成 `refactor(review)` 提交。触发条件（任一）：重大变更——触及安全面 / IPC 帧契约 / 发版链路 / 跨模块行为；或用户指令的批量形态——对指定提交范围做事后审核。
6. **提交**：逻辑单元分粒度提交，conventional commits 格式。
7. **推送 + 观察 CI**：main 推送触发 ci.yml，绿了才算完；涉及打包/workflow 链路的按步骤 3 加跑实跑验证。
8. **收尾（每个功能批次完成即执行，不积压到会话结束）**：按 [session-close](session-close.md) 检查单过一遍本批次相关项（提交对账 / 交接条目 / 待办速览）；ADR 保持与 shipped 现实一致的 implemented 表述。
