# Agent Note: 尺寸健康闸 F1 降为 report-only 评审信号

Status: implemented

Review: FULL/2026-10-28/R1=ok R2=ok R3=ok

## Problem

F1（单文件 >400 物理行）以 `--enforce` 硬接 pre-commit（`.githooks/pre-commit`）与 CI（`ci.yml` docs 作业）——超线即红。行数是代理指标不是不变量：为了不被它拦，拆分开始由帽值而非内聚驱动【推断 · 未证——观测层是下面三条，因果方向由其集群形态推出】。实证三条：仓库最大的三个文件 397/394/393 行全部贴着 400 死线（压帽切的指纹）；partial 类家族 22 个文件（`HarnessRuntimeHost` 拆 4 片、`RuntimeBootstrap` 与 `DshShellForward` 各 3 片）；`3d754c6` 冷启动性能修复同批把 `DshShellForward.Gate.cs` 的探活逻辑切进新文件 `Probes.cs`（+120 行）——性能修复伴随结构手术【推断 · 未证：与 F1 帽的因果联系是时点共现，未单独证实】。门禁自身判据已经承认这个害处：F3 对装配根特意改用合计判据——单文件 400 的判据对根集检不出碎片化，根的总量仍有增殖空间（ADR `2026-09-28-compose-root-form-separation`），同一论据没有推广到全域。

## Decision

F1 降为 report-only：`_violations` 拆双车道（enforced = F2/F4，report-only notes = F1），`--enforce` 只对 F2/F3/F4 退出 1；F1 note 在两种模式下都打印（CI 日志仍可见拆分压力信号），阈值 `--file-limit 400` 不变。`docs/architecture-standards.md` R4 与根 AGENTS.md 门禁清单同步改写；feature-flow 步骤 4 的「先拆再上」强制收窄到强制阈值（F2/F3/F4），F1 归评审判读。硬闸保留下来的都是更准的上帝对象信号：方法级长度（F2 >80 行、F4 装配面方法 >60 行）与防腐边界（F3 装配根合计 ≤500）。

## Alternatives considered

- **F1 阈值提到 ~800 保持强制**：落败——仍是代理指标硬闸，压帽切分的动机原样保留，只是换了切分点，拆分继续由帽值而非内聚驱动。
- **完全删除 F1**：落败——行数信号对评审仍有价值（本批动因就是拆分压力的可见性），删除丢失信号；report-only 保留信号且零门禁成本。
- **partial 簇合并计量（一个家族当一个虚拟文件）**：落败——簇识别靠启发式，把机器可查性复杂化；且 F1 降级后压帽拆分的动机消失，该机制挡不住任何正在发生的变更（R6 第一问）。

## Consequences

- 单文件超 400 行不再红 CI/pre-commit；超长文件的兜底从机器闸退到三审 R4 评审项——比机器闸弱（评审有漏过先例），缓解是 F2 方法帽仍硬、F1 note 仍在 CI 日志可见、R4 评审项仍在。
- 当下零回归：现存最大文件 397 行 < 400，本变更不改变任何现有文件的判定状态。
- 顶帽拆分的动机消失；已有 22 个 partial 家族不回并（内聚理由成立的保留，纯压帽切的合并是否值得做属后续 simplification 立项，不在本批）。

## Testing

- `--self-test`：F1 夹具断言改为「note 车道有、enforced 车道无」，F2/F3/F4 断言与 F3 的「F1 不得误发」守卫保留，全部 ok。
- `--enforce` 实跑 exit 0（当前仓库无文件超 400，与旧闸行为一致）；report-only 输出形态（note 打印 + exit 0）由 self-test 与夹具覆盖。
