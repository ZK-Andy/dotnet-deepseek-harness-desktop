# 会话开场检查单（session-open）

> 每次会话恢复/开始时顺序执行；全部完成后向用户复述关键状态并等待命令。

1. **读交接文档家庭（本地工作文档，未提交；文件名即语义锚点，克隆后不存在属预期）**——入口文档「**状态区**」（背景/位置/当前状态/待办/开始步骤）+「**交接更新记录**」摘要滚动窗；**待办明细读其待办文件**（`[ ]` 条为行动清单、`[x]` 为近期窗口的一行指针，越窗指针在当月冷归档卷）；如需近期过程细节，再读本地 journal 对应**月卷**（按月归档）。踩坑判别要点见 `docs/cookbook.md`（踩坑与经验的家）。
2. **git 对账**：`git log --oneline -8 && git status`
   - HEAD 若比 HANDOFF 最新记录**多出提交**：逐条查明内容再继续（教训：未记录的提交曾导致决策误读）。
3. **门禁基线**：verify-adr-format / verify-doc-budgets / verify-md-links / verify-handoff-structure 四脚本全绿（后者在清检 CI 无 HANDOFF 时自动跳过）。
4. **.NET 基线**：`DOTNET_CLI_HOME`/`NUGET_PACKAGES` 重定向后 build + test 全绿（0 警告）。
5. **声明会话模式**：按 [session-modes.md](session-modes.md) 与用户确认本轮类型与边界。
6. 向用户复述：关键状态、当前待办、相关踩坑判别（见 `docs/cookbook.md`）；然后等待命令。
