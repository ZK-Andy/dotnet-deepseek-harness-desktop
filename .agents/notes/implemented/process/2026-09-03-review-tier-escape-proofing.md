# Agent Note: review-tier-escape-proofing（评审档判定机械强制——行为加机械限制，防执行者自觉逃逸）

Status: implemented

Review: FULL/2026-09-03/R1=ok R2=ok R3=ok

## Problem

2026-09-03 组合根阶段方法重构（composition-root-stage-typing）暴露评审档判定的逃逸：该 diff 触碰 `DesktopBootstrap`（组合根核心启动链）×3 + `ArchitectureTests`（A5 门禁判据放宽）+ 其 proposed ADR 明确承诺「三重审核（R1/R2/R3 串行）」，但主会话套用 review-scope-narrowing 的通用「零行为变更 → 轻审 R2」缺省判据，只跑了一路 R2 轻审即放行——用户指出时，评审档判定仍是**执行者自觉**：规则写得再细，执行者自判「这算纯结构重构」即可绕开。补丁若只加文字条款（T1–T6）必然重演：本次逃逸恰是「规则已写明但仍被绕开」。

## Decision

评审档判定已**机械化**，行为加机械限制：`scripts/verify-review-tier.py`（本 ADR 的单一事实源规则实现）已落地，接 pre-commit / pre-push / CI（docs job）三门禁。档位分类**无执行者自由裁量**，由 diff 触达面自动判定：

**FULL（须三重审核 R1/R2/R3，证据不齐即机械拦截）**——命中任一路径模式：
- **组合根级**：`src/**/DesktopBootstrap*.cs`、`src/**/Program.cs`（组合根时序回归固有高，即使纯结构重构）。
- **专项 ADR 承诺**：变更的 **proposed** ADR 正文含「三重审核」承诺——专项承诺 > 通用缺省判据，主会话不得以「零行为变更」覆盖。
- **门禁判据改动**：`tests/**/ArchitectureTests.cs`、`scripts/verify-*.py`、`scripts/verify-*.sh`、`.editorconfig`、`.githooks/**`（删 hook 接线 = 绕过门禁的最廉价路径，故 hooks 自身改动也归 FULL）——改门禁 = 改结构约束，非「纯测试断言调整」。
- **行为契约面**（src/tests 之外）：`.github/workflows/**`、`resources/**`、`templates/**`、`docs/**`。

**LIGHT（可轻审 R2）**：其余。

**Review 证据（机器可查，须随变更携带）**：变更集内（同批暂存/提交）的 ADR 头部含
`Review: FULL/yyyy-mm-dd/R1=ok R2=ok R3=ok` 行——证据必须与触发变更**同批**，仓库历史中的旧证据不清除新的 FULL 变更。`--enforce` 下 FULL 档 diff 无证据 → exit 1 拦截。执行者想「轻审跳过」必须物理改门禁（改脚本本身又触发 FULL 的 gate-criteria 规则）——逃逸成本从「自觉」升为「对抗机器」。

**既有判据关系**：本机制不取代 review-scope-narrowing 的触发枚举/零行为变更判据（判据仍在彼处），而是其**更高档优先**的机械前置——FULL 档路径命中即强制重三审，通用轻审判据不得覆盖。脚本分类规则为本机制单一事实源；文字细则见 [review-scope-narrowing](../../implemented/process/2026-08-31-review-scope-narrowing.md)（已加指针）。

## Alternatives considered

- **纯文字补丁（T1–T6 条款写进流程卡/ADR）**：E1 逃逸正是「规则写明仍被绕开」——执行者自判档位时心理捷径压倒条款，再加十条文字也拦不住下一次。落败；必须机械限制。
- **hook 拦截靠人肉跑脚本**：脚本存在但由执行者自觉运行 = 无限制。落败；必须接进 pre-commit/pre-push/CI 自动跑、缺证据 exit 非 0。
- **档位判定交给评审代理自报**：评审代理（AI）判定自己该不该被审，存在与被审对象同源的系统性盲区。落败；档位由脚本按 diff 路径机械判定，与任何代理的自觉无关。
- **FULL 档证据要求完整 Review 报告文件**：机器难验「报告真伪」，易被空壳文件糊弄。落败；证据缩为 ADR 头一行 `Review:` 标记 + 三重审核实际执行留痕在 ADR 正文（R 结论由评审代理产出、主会话收口），机器只验「有标记」、真伪靠评审闭环。

## Consequences

**收益**：评审档判定从执行者自觉变为机器强制；组合根级/门禁判据/承诺三重审核的 ADR 三类高风险变更，缺三审证据即无法 commit/push（pre-commit `--staged` + pre-push/CI `--since` 双闸）。本次逃逸形态已被脚本在真实仓库复现拦截（9 触发点：3 组合根 + ArchitectureTests + 脚本自身 + escape-proofing 承诺 + pre-commit/pre-push 两 hook 接线）。

**代价**：
- FULL 档变更加一次「证据随变更」义务——组合根日常小改（如注释）也会触发 FULL 需证据。缓解：组合根小改本属高风险面，宁可拦错不漏；纯注释可并入带证据的批或走简化路径前先补证据 ADR。
- 证据 = ADR 头一行 `Review:`，机器不验「评审真伪」——防的是「跳过评审」，不防「虚假评审」（后者靠评审代理独立判读 + 主会话收口，本仓既有契约）。
- `scripts/verify-*.py` 全部归 gate-criteria → 以后改任何 verify 脚本都要 FULL。缓解：这是刻意的自我钳制（门禁自身改动最高危），改门禁脚本须先给本批证据。

## Testing

`python3 scripts/verify-review-tier.py --self-test`：10 隔离夹具全绿（LIGHT 通过 / FULL 组合根拦截 / 证据随变更放行 / gate-criteria 分类 / proposed ADR 承诺分类 / `--since` 抓 outgoing 已提交变更 / 范围内证据放行 / 重触旧证据窗口 best-effort / `R1=fail` 不计数 / proposed 自证不放行）。真实仓库复验：本批 diff 判 FULL 且 `--enforce` exit 1（9 触发点被拦，因缺证据载体）。

## Related

- [review-scope-narrowing](2026-08-31-review-scope-narrowing.md)（implemented）：被本机制覆盖「更高档优先」判据的母 ADR，已加防逃逸指针。
- [feature-flow](../../../workflows/feature-flow.md) 步骤 5：评审执行契约入口，指向 review-scope-narrowing。
- [composition-root-stage-typing](../../proposed/architecture/2026-09-03-composition-root-stage-typing.md)（proposed）：本次逃逸的触发变更，其三重审核已随本批补跑完成（R1/R2/R3 收口）。
