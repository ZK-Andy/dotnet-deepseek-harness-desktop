# Agent Note: post-packaging-churn-restructure（三平台打包震荡后的结构清理总纲）

Status: proposed

## Problem

三平台打包震荡期（2026-09 下旬）暴露三处结构性缺陷，CI 全绿但结构持续劣化：

1. **组合根「注册」与「编排」从未分离**：容器只装 Ryn 叶子，自有服务全手 `new`，启动编排写成根上字段 + 阶段方法链；F3 每文件 400 行预算使增长必然转化为分部增殖——12 天内 `729 行/2 文件 → 1074 行/7 文件`（+47%），门禁仍全绿。
2. **门禁只查形状不查语义**：14 道门校验格式/骨架/链接/尺寸，无一校验「文档陈述 == 代码事实」——3 篇 ADR 核心产物已删仍挂 implemented、5 处 CI 步骤永不可能失败、8/16 条归档链接死链而链接门禁正好排除该目录。
3. **脚本层零规范**：三平台复制粘贴成为唯一手段，共享库被手抄 + 影子副本遮蔽且已分叉。

另有 Blocker 级现行错误：B1 发布门 arm64 常开、B2 落定窗死配置（300s 被钳 120s）、B3 架构单一事实源描述「代理层之前」的启动模型、B4 冒烟 tag-only 导致 PR 零覆盖。

完整诊断（每条论断带 `文件:行`）见方案原文：`整改方案-三平台震荡后结构清理-2026-09-27.md`（本地工作文档，未提交，与 HANDOFF 同层）。

## Proposal

分七批执行（每批携带自己的 ADR；本篇为总纲与被引用点）：

- **A** 冒烟口径单一事实源 + 发布门诚实化（§3）
- **B** CI 三流合一 + 门禁诚实化 + actionlint/shellcheck 进 CI（§6）
- **C** 脚本规范 + 共享库收口 + 三包/三烟重构（§5）
- **D1–D4** 组合根形态分离（注册下沉 `AddXxx()` + 编排成 `IStartupSequence`）→ F3 换集合计闸 + `verify-compose-root.py` → 端口补齐 + `RuntimeSupervisor`/`UpdateCoordinator` 迁 Core → 值流/死代码清理（§4）
- **E** ADR 裁定执行 + architecture.md 重写 + 事实单一家 + 死链重写 + slop 清洗（§7）
- **F** Python 门禁共享模块 + 覆盖率比对数（§5.4、§6.2）
- **G** 测试工程归位（§4.6）

已拍板决策：组合根取完整形态分离 (a)；冒烟 PR 只跑 Linux 腿 (b)；脚本命名保留 kebab-case、尺寸闸 250 行、shellcheck/actionlint 进 CI、action SHA 钉版暂不做；arm64 冻结直接解冻（冻结决策早于链路转向，承载 hang 的旧链路已删，退出条件已死）。预算侧：下调 `notes/README.md` 800→400、`coding-standards.md` 1000→700，给 architecture.md 腾空间；冒烟自测接线。

**执行状态（2026-09-28）**：A、B-1、B-2、C、D1、D2、D3-a、D3-b、D4 已交付（各自 ADR 见 implemented/）；E 已交付（ADR `process/2026-09-28-doc-adr-layer-cleanup`）；F、G 待做。全部批次完成后本篇改写为 implemented。

## Alternatives considered

- **只修 Blocker 不动结构**：B1–B4 逐个热修最快，但组合根增长机制（每文件预算 → 分部增殖）与门禁盲区不动，两周后重演。落败。
- **引入 Generic Host / `IHostedService` 让容器驱动启动**：Ryn 无 hosted-service 机制且 `RunAsync` 强制 thread 0；技术不成立且已被两次否决。落败——编排搬出根但仍由根显式触发。
- **给 Core 加 DI 容器依赖统一注册形态**：R3 端口构造注入即可，Core 零外层姿态（R2）不为此松动。落败。
- **维持现状只调闸（放宽 F3 或收紧数值）**：判据本身错了（「文件多长」≠「根里放了什么」），调数值不改变增殖机制。落败。

## Acceptance criteria

- 全部批次交付且各自 ADR implemented；本篇改写 implemented。
- 组合根集合计 ≤500（F3 新闸）且根内形态过 `verify-compose-root.py` 四查。
- 每道 CI 门存在能失败的路径（门禁诚实化）；冒烟进 PR（Linux 腿）。
- 文档层：事实单一家清账、归档死链清零、slop 清零、architecture.md 描述当前启动模型。

## Risks

- 批次间耦合：D1 必须先于 D2（先分离再立闸，否则当场红）；E 依赖 D（文档反映新结构）。
- FULL 档评审成本高（本方案几乎所有批次命中 FULL 面）——利用「迁 Core 后续批只落 `src/**`+`tests/**` 不再命中 FULL 面」降本。
- cookbook 贴顶（2700 上限余 8.6%）可能迫使条目寄存 ADR；已靠冷归档层腾词缓解。
