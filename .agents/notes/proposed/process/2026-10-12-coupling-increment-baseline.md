# Agent Note: 耦合增量闸基线记账

Status: proposed

## Problem

耦合左移三闸只落了第①闸（注册点扫描）：第②闸（PR 增量闸：依赖扇出 diff + 单文件单 PR 净增上限，对标上游 `benchmark-next-package-dependency`）缺两样东西——耦合来源的分类记账（阈值按什么维度定）和本仓的基线分布（阈值定多少）。`docs/cookbook.md` 当时只有 1 条注册点判别、无分类数据，且主档字数贴顶（2804/2804），加记账须先腾预算。

## Proposal

1. 分类法五维（判据 + 本仓当前计数，2026-10-12 实测）：①注册外直构 R1a=1（`StartupNoticeService`，`StartupSequence.Supervision.cs:146`，帽 1/1）②注册外静态调用 R1b=11/15（`Autostart` 2、`StartupSequence` 2、`DesktopBanner` 1、`ExternalLink` 1、`PagePump` 1、`RynNavigationCallbacks` 1、`Diagnostics` 3；组合根收官顺手清掉 4 处，余 4）③层内跨域引用（零机器覆盖，本批未量测到新增）④BCL 直调绕边界 D005（`verify-code-conventions.py`，零命中）⑤单 PR 净增膨胀（无闸，R4 不设上限）。
2. 基线分布（n=15，近 25 个提交抽样，`git show --numstat/-U0`）：功能批 gross 多在 270–300、最大 520（`800492a` 298+/221-）；净增 +80–+260；单文件净增最大 136（测试）/100（脚本）/73（产品代码 `RunServicesRegistration.cs`）；新增 `Infrastructure.` 引用/提交多为 0、最大 2（`800492a`），新增 `PackageReference` 全 0。
3. 阈值区间草案（本篇不定死，落地时再钉）：单文件净增 >150 或单 PR gross>600 即需拆分说明；单 PR 新增 Infra 引用 >2 或新增包引用 >0 即进评审。
4. 预算：cookbook 主档内压缩腾挪（本批压三条最贵的活跃判别约 90 词、新增记账约 70 词，不碰冻结 archive、不提额）；记账条目进 `[调试]` 节。
5. 落地后再议：R1b 帽 15→11/12 下调属第①闸维护，不在本篇范围。

## Alternatives considered

- **单文件净增 >100 即红**：按基线产品代码最大 73、测试 136 计，100 会把正常测试/脚本提交也拦下，误拦成本高于左移收益。落败（取 150 作告警线而非红线）。
- **只做扇出闸、不做净增闸**：扇出（新增引用/新包）挡"放错地方"，净增挡"一次涨太多看不过来"；基线显示扇出几乎恒 0（15 抽样最大 2），只做扇出等于闸永不触发。落败（双闸并行，净增为主、扇出为辅）。
- **cookbook 提额容纳记账**：manifest 允许附理由提额，但本批可压缩量充足（top3 条 443 词全是可压复述）；无压缩先提额违反"迁移→精简→才提额"顺序。落败（暂）。
- **archive 层腾预算**：archive 是冻结层、只收退役机制条目，活跃判别移入即污染冻结语义。落败。

## Acceptance criteria

- `docs/cookbook.md` 有五维记账条目且 `verify-cookbook.py` 通过。
- 字数预算内：`verify-doc-budgets.py` 全绿（无提额）。
- 本篇 proposed 落盘且 `verify-adr-format.py` 通过；`HANDOFF-todos.md` 第①条更新为"已记账、阈值待定"。
- 第②闸实现（脚本 + 帽） explicitly 不在本篇验收内，留后续 ADR。

## Risks

- 基线 n=15 属小样本【探索性】：阈值区间只是草案，钉死前需更大样本或等自然触发复核。
- 层内跨域引用仍零机器覆盖：分类法第③维是记账占位，不是执行；别误当已落地。
- R1b 余 4（11/15）是 slack 不是目标：降帽需另走脚本变更（FULL 面），本篇不动帽。
