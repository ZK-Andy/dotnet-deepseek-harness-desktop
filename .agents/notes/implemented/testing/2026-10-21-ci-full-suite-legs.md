# Agent Note: CI 三平台测试腿跑全量套件

Status: implemented

Review: FULL/2026-10-21/R1=ok R2=ok R3=ok

## Problem

`ci.yml` 的 `build-test` 矩阵里只有 ubuntu 腿跑全量 `dotnet test`（带覆盖率收集），windows/macos 腿的命令带 `--filter "FullyQualifiedName~StalePackagePrunerTests|FullyQualifiedName~ReleaseAssetTests"`——真内核上只执行两个测试类，`Desktop`/`Core`/`Infrastructure`/`Analyzers` 四个测试工程在 Windows/macOS 上零执行。这两类测试的存在理由是它们依赖 OS 级语义（`FileShare.None` 跨进程锁、资产锁），但过滤条件把「覆盖率基线只在一条腿收集」悄悄扩大成了「其余腿只验 OS 语义相关的两类」：任何在 Windows/macOS 上才会暴露的行为（路径分隔符、大小写不敏感、行尾、文件锁差异、进程关闭顺序）都只有用户装包后才发现。同时审计建议把 `packaging/**` 补进 `paths-filter` 的 code 面，理由是「改 Inno 模板的 PR 整条 build-test 跳过」。复核后这条不成立：`docs` job 无条件执行，且已列 `bash scripts/package-windows.sh --self-test`（[`ci.yml`](../../../../.github/workflows/ci.yml) 的「脚本自测」步），该自测离线渲染 `packaging/windows/installer.iss.in` 并对占位替换与关键行形状逐条断言（[`scripts/package-windows.sh`](../../../../scripts/package-windows.sh) `iss_self_test`）。模板改动每次 push 都被执行一遍；把 `packaging/**` 加进 code 面只会让改模板的 PR 多跑一遍三平台 .NET 矩阵，而 Inno 真编译（`ISCC.exe`）本来就只在 Windows 打包腿上发生，code 面命中与否都不改变这一点。

## Decision

- windows/macos 腿的命令去掉 `--filter`，与 ubuntu 腿跑同一条 `dotnet test dotnet-deepseek-harness-desktop.slnx -c Debug --no-build`；步名改为 `test (full suite on OS-level semantics legs)`，矩阵注释写明「全量套件在三个真内核上都跑…另两腿不设过滤，四个测试工程在真内核上一并执行」。
- 覆盖率收集与 `format --verify-no-changes` 仍只在 ubuntu 腿（基线单值，多腿并集不增加判据；format 是平台无关的 Roslyn 分析，单腿即穷尽）；`paths-filter` 的 code 面**不加** `packaging/**`——模板的门是无条件 `docs` job 的渲染自测，加面零检出。

## Alternatives considered

- **维持两测试类过滤**：落败——它把「覆盖率单腿」与「其余腿只验两类」混成了一个判据；`Desktop`/`Core` 的行为在 Windows/macOS 上从未被执行，真内核语义测试的存在反而掩盖了这个空洞。
- **三腿都收覆盖率、按并集比对基线**：落败——基线数字是单值且由 ubuntu 腿产出（[coverage-baseline-from-ci-cobertura](../testing/2026-09-12-coverage-baseline-from-ci-cobertura.md)）；并集口径要重定义基线、重算历史并解释跨腿差异，换不到新的删错能力。
- **只挑「看起来 OS 敏感」的测试类在 Win/mac 跑**：落败——挑选依据正是当前 filter 的拍脑袋形态，没有覆盖率证据支撑；宁可整腿跑全量，红了再按真实失败收窄。
- **把 `packaging/**` 加进 code 面（审计建议）**：落败——证据见 Problem 段：模板渲染在无条件 `docs` job 已执行，code 面命中只多跑三平台 .NET 矩阵，不增加模板检出；与 [delivery-chain-gaps](2026-10-20-delivery-chain-gaps.md) 里同一条 Alternatives 的结论一致，本批复核后维持。
- **给 `packaging/**` 单独开一个只构建 Windows 安装器的 PR 触发腿**：落败——Inno 编译需要 Windows runner 与 ISCC，代价是分钟级且与 tag/dispatch 打包腿重复；模板结构错误由离线渲染自测先拦，真编译留给打包腿。

## Consequences

- windows/macos 腿的 CI 时长上升（全量套件替代两个类），本批想要的红是平台假设（路径、大小写、行尾、锁语义）当场暴露；`fail-fast: false` 保持，三腿结论独立。
- 模板改动的验证深度不变：离线渲染 + 关键行断言每次 push 跑；`ISCC.exe` 真编译仍只在打包腿，属已登记的交付链残留（[delivery-chain-gaps](2026-10-20-delivery-chain-gaps.md)）。

## Testing

- 本地：`python3 scripts/verify-governance.py`（工作流注入面）与 `actionlint -shellcheck="shellcheck -S warning"` 对改后的 `ci.yml` 全绿；三腿全量的实跑证据在 push 后的 `ci.yml` run（本机无 Windows/macOS，两腿的首个执行点是该 run）。

## Related

- [delivery-chain-gaps](2026-10-20-delivery-chain-gaps.md)——同一判据面的上一批补洞（Linux 载荷探针、发布总检自测、`tools/**` 扩面），本篇复核其 `packaging/**` 结论后维持。
- [ci-gate-honesty](../process/2026-09-28-ci-gate-honesty.md)——「路径过滤不得漏判结构级输入」的机制来源；本篇是它的反向用例。
- [cross-process-filelock-semantics](2026-09-13-cross-process-filelock-semantics.md)——OS 级语义测试类的存在理由（本 ADR 让它们在 win/mac 腿随全量套件执行）。
