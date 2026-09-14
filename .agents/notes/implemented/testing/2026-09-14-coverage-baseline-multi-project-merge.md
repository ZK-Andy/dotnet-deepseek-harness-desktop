# Agent Note: 覆盖率基线合并多测试工程 cobertura

Status: implemented

Review: FULL/2026-09-14/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`dotnet test <sln>` 每个测试工程起一个测试宿主，`--collect:"XPlat Code Coverage"` 因此每个工程各产一份 `coverage.cobertura.xml`。测试拆成主工程/Core/Infrastructure 三工程后，CI `coverage summary` 步原本的 `grep line-rate ... | head -20` 打出三行分片值（0.1743 / 0.5736 / 0.5405）——每份文件都含它引用程序集的全部源行（未被该工程测试触及的记 `hits=0`），单份文件的 `line-rate` 只描述该工程切片。基线（README 双语徽章 + `docs/testing.md` 基线行）若照旧取一行即失真，取哪一行也没有口径。

## Decision

覆盖率基线 = 全部 cobertura 的**并集**：键 `(assembly, 源路径, 行号)`，hits 取跨文件最大值（覆盖 = `hits > 0`）。coverlet 在不同工程写出的路径前缀不同（`<Assembly>/Services/X.cs` 与 `Services/X.cs`），键形成前剥掉与 package（程序集）名相同的首段，否则同一物理行按两个键各计一次。

新增 `scripts/coverage-summary.py` 承担合并并打印机器可解析的基线行 `coverage-summary: covered=<c> valid=<v> line-rate=<rate> (<pct>%)`；无 cobertura 文件即 exit 1（缺失产物不得打印看似合理的 0%），XML 损坏 exit 2。`ci.yml` 的 `coverage summary` 步调用该脚本。脚本自带 `--self-test`，夹具钉住两个判据点：跨文件 max hits 不因某片 0-hits 而失去命中、程序集前缀归一后同物理行合并为一个键。

同批完成 B1/B2/B4 挂账的标准跟值：基线取 CI `34793499164`（B4 代码面）合并值 `4040/7306 = 55.30%`，README 双语徽章与 `docs/testing.md` 基线行随值更新，测试数 612→614。

## Alternatives considered

- **用标准合并工具（ReportGenerator `dotnet-reportgenerator-globaltool` / first-party `dotnet-coverage merge`）**：落败——两者都须在 CI 引入工具安装与版本维护面，其合并语义与输出随工具版本走；自带自测的 Python 脚本可离线按同一件 artifact 复算，与既有 `verify-*.py` 自测形态一致。
- **只取主测试工程的 cobertura**：落败——该文件 `line-rate` 0.1743，只含组合根测试触及的行，不描述全量覆盖。
- **三份 `line-rate` 取平均或取最大**：落败——分片各自的有效行集合重叠（Core 被三份文件都含、Infrastructure 被两份含），算术合并无定义。
- **键不归一化（直接用 coverlet 原路径）**：落败——同一行在 `DeepSeek.Harness.Desktop.Core/Services/X.cs` 与 `Services/X.cs` 两个键下各计一次，有效行由 7306 虚增到 8087、比率压到 52.94%。
- **只让一个测试工程收集覆盖率**：落败——其余工程的测试不再计入覆盖与测试数，是数据缩水而非口径修正。

## Consequences

- 基线口径与测试工程数解耦：新增测试工程各自产 cobertura，`coverage summary` 步自动并集。
- `coverage-summary.py` 的输出行成为机器可解析契约；`docs/testing.md` 的复现口径随本批增补该脚本与 artifact 复算两步。
- 本批 `src` 零行为变更；测试只改一处 XML doc `cref`（`HarnessRuntimeHostTests` 指向随工程拆分搬迁的 `SharedHomeContractTests`，消 CS1574 警告，恢复 0 警告门）。
- 合并值 `4040/7306` 相对上一基线 `3994/7158`：有效行 +148 来自拆成三程序集后各程序集的源生成文件（JSON/Ryn/Regex 生成器）各计一份，覆盖行 +46；比率 55.79%→55.30% 属口径面扩张，非覆盖回退。

## Testing

`python3 scripts/coverage-summary.py --self-test`：7 条断言（前缀归一后同键、跨文件 max hits、独立 package 各计、按 package 分解；另三条 fail-loud 退出分支——malformed XML→2、0 有效行→1、无文件→1）。真实 artifact 复算：对 CI `34793499164` 的 `coverage-cobertura` artifact 跑 `--results` 得 `4040/7306 = 55.30%`，与 B4 作业打印的三分片值按并集一致。

## Related

- [覆盖率基线取 CI cobertura 实测](2026-09-12-coverage-baseline-from-ci-cobertura.md)：基线来源与人工跟值义务；本批的跟值台账行在该篇。
- [README 事实徽章与基线行一致性机械校验](2026-09-13-readme-badge-baseline-gate.md)：断言两个家相等，值的来源由本篇与上篇定。
- [clean-architecture-b4-finalization](../architecture/2026-09-14-clean-architecture-b4-finalization.md)：三工程镜像拆分（本批合并口径的触发面）。
