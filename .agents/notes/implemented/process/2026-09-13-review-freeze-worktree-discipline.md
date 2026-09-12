# Agent Note: 评审对象冻结纪律（工作树冻结在暂存集）

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

[review-evidence-freshness-gate](2026-09-13-review-evidence-freshness-gate.md) 把「证据须本批新产」机械化后，评审启动面还剩一个机器不管的洞：**评审对象本身可能不是将提交的内容**。2026-09-13 门禁批的 R1/R2 五轮评审里实测踩中两形态：

- **冻结后漂移**：候选评审对象冻结后又有文件改动且未重新 `git add`——评审代理拿 `--staged` 的 index diff，审的是 index，工作树里是另一版。评审结论对不上将入库内容，修复也会基于漂移后的 diff 收口。
- **来源不明 blob 入暂存**：一份出处不明的历史 ADR 副本被顺手 `git add` 进 index（副本只存 `.plan/stray-staged-composition-root-proposed-2026-09-13.md`），暂存集混入非本批内容——若非主会话自查发现，评审与提交都会把陌生历史版当本批变更。

`verify-review-brief.py` 是评审启动的唯一机器闸（feature-flow 步骤 5：启动任何评审代理前必跑 `--enforce`），但只校验简报形状，不校验工作树状态——「每轮启动前自证只有暂存项」此前只是文字义务，而本仓已多次实证「写明规则不构成限制」。

## Decision

评审对象冻结纪律机械化，落在 `scripts/verify-review-brief.py`（评审启动门禁即执行点，每轮启动前必跑）：

- 新增工作树冻结校验：`git status --porcelain` 的每行都必须是**纯暂存项**——XY 两列中 Y 列（工作树列）非空格即违规（冻结后改动未重暂存），`??` 未跟踪行即违规（来源不明文件被顺手 add 的入口形态；briefs 与 `.plan` 等忽略路径不出现，不受影响）。
- 校验随 `--enforce` 一并拦截：违反即先重暂存/清理再启动评审，未冻结不得起评审代理。
- 校验在 `main()` 直挂真实仓库，不进 `check_repo()`——自测夹具是非 git 临时目录，档位/简报判定与工作树状态是两个独立关注面。
- [feature-flow](../../../workflows/feature-flow.md) 步骤 5 加硬条款：每轮评审启动前自证冻结，简报 Scope 的 base/head 与冻结集一致。
- 判据不是把「审 index 还是工作树」二选一——两形态各自成立：index diff 是 `--staged` 档位判定与评审 diff 的读取面，工作树是会话里继续被编辑的面；纪律是让两者在评审窗口内**一致且不再漂移**。

## Alternatives considered

- **只写文字条款进 feature-flow，不机械化**：落败——与 owning 批次同源教训（2026-09-12 证据绕过、E1 逃逸）：写明规则不构成限制，启动门禁已有执行点，机械化边际成本极低。
- **只拦工作树漂移、放过未跟踪文件**：落败——②形态的入口正是未跟踪文件被顺手 `git add`；且未跟踪文件的存在本身就说明工作区不冻结，放行等于只堵一半。忽略路径（briefs/.plan）天然不出现在 porcelain 输出，无误伤。
- **放进 `check_repo()` 与简报判定合并**：落败——`check_repo` 的自测夹具是非 git 目录，混入会把「简报形状」与「git 状态」两个失败面搅在一起；分离后各测各的。
- **改由 `verify-review-tier.py` 承载**：落败——tier 脚本在 CI（`--since` 模式）也跑，工作树冻结是**本地评审启动**语义，CI 无工作树概念；brief 脚本 docstring 明确其为 local pre-launch check，语义对位。
- **要求评审代理自己跑 `git status` 自证**：落败——冻结是主会话在启动**前**的动作，代理拿到简报时漂移已发生；把义务压给被启动方等于无人执行。

## Consequences

- 收益：「每轮启动前工作树冻结在暂存集」成为机器判据；冻结后漂移与陌生文件入暂存两形态在启动闸即拦下，评审对象 = 将提交内容有机械背书。
- 代价：评审窗口内任何工作树编辑（包括评审修复期间）后重启下一轮，必须先重暂存——这是刻意摩擦，正是坑 ① 的反向约束。
- 代价：有正当理由的非忽略未跟踪文件在场时会拦（需先 `git add` 纳入本批、或移走/忽略）——方向是拦住而非放过。
- 边界：`--since <base>` 模式（已提交批次的事后审核）下工作树冻结无对应语义，本判据只在本地启动闸生效，不进 CI。
- 边界：暂存集内容是否**正当**（是否本批该有）机器不判——②形态里文件已在暂存集内时判据放行，靠简报 Scope 的 base/head 与文件清单对账；机械化到「暂存集 = 预期清单」需要意图输入，不在此批范围。

## Testing

`python3 scripts/verify-review-brief.py --self-test`：9 例夹具全绿（原 8 例 + 新增工作树冻结例）——真实临时 git 仓库：纯暂存状态放行；已跟踪文件冻结后改动未重暂存判违规；补 `git add` 后放行；非忽略未跟踪文件判违规。真实仓库复验：干净工作树 `--enforce` exit 0；手造一处未暂存改动后 `--enforce` exit 1 且报「re-stage or discard」；恢复后 exit 0。

## Related

- [review-evidence-freshness-gate](2026-09-13-review-evidence-freshness-gate.md)：同日同源批次——证据 freshness 已机械化，本篇补评审**对象**冻结面。
- [review-scope-narrowing](2026-08-31-review-scope-narrowing.md)：简报表与启动门禁（本判据的宿主脚本即其 `verify-review-brief.py`）。
- [review-brief-gate-self-assertion](2026-09-04-review-brief-gate-self-assertion.md)：同宿主脚本的另一校验面（简报门禁自证 vs 工作树冻结）。
- [review-tier-escape-proofing](2026-09-03-review-tier-escape-proofing.md)：档位机械判定；`--staged` 读 index 是本判据「审 index = 将提交内容」等式的前提。
