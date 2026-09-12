# Agent Note: 覆盖率基线取 CI cobertura 实测

Status: implemented

Review: FULL/2026-09-13/R1=ok R2=ok R3=ok

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

`README.md` / `README.en.md` 徽章与 `docs/testing.md` 基线行描述同一事实（覆盖率），取值来源却未定义：既有做法是「本机以 CI 同款命令实测、取 cobertura 的 `line-rate`」。该数在本次实测条件（只读 home 的本机，2026-09-12）下偏高 4 行，且随代码面推进要有人手工重算——徽章因此停在旧值 55.37%（当时只记 `line-rate=0.5537`，按该代码面 6722 有效行折算 ≈ 3722 覆盖行【折算值，非直接测量】；同一代码面的 CI 作业 `34641155703` 记 3718/6722 = `line-rate=0.5531`，折算值与该 CI 值差约 4 行）。

差值不是代码差异，已定位到行：逐类比对**当前代码面**的两份 cobertura（CI 作业 `34648102527` 的 artifact `coverage-cobertura` 3714/6725，对本机 `TestResults` 3718/6725）得**唯一的覆盖状态差异** = `Services/HostLog.cs:40-43`（另有 4 个文件的命中计数不同，但不翻转覆盖状态），即 `HostLog.Write` 的落盘失败 `catch`。命中剖面把抛点钉到一行：两处 `Write` 各调用一次（第 19 行 hits=1、方法入口 hits=2），同方法内 `Directory.CreateDirectory` 与 `RotateIfNeeded` 两次都正常返回，而 `File.AppendAllText`（第 37 行 hits=2）之后 try 闭合行只剩 hits=1——本机那次抛了，CI 的同一调用写盘成功（try 闭合行 hits=2、`catch` 全 0）。【推断 · 未证】抛因是只读 home：本机 `/home/<user>` 不可写（`touch` 报 `只读文件系统`），CI runner 的 home 可写。这条 `catch` 只反映环境能否写盘，不反映被测行为。

测量设置（2026-09-12）：CI——`34641155703`（0.4.9，含 PR #1 之前的代码面）3718/6722 = `0.5531`；`34647696070`（PR #1 合并）与 `34648102527`（0.4.10 bump）指向 PR #1 之后的同一代码面，两作业逐字一致地记 3714/6725 = `0.5522`。本机——`-c Debug --collect:"XPlat Code Coverage"` 本批两次（artifact `TestResults/coverage-baseline`、`coverage-repeat`）均 3718/6725 = 55.29%【探索性，n=2】；同日更早一次的本机记录为 3721–3722/6725（相对 CI 的 3714 是 +7~8 行），其 artifact 已不在盘、不可复核，与本节那个 4 行差不对账——本机值随环境态变化，正是本决定要摆脱的变量。本机同命令 `-c Release` 两次均 2724/4891 = 55.69%【探索性，n=2】，序列点集合与 Debug 不同口径（有效行 4891 对 6725），不与徽章可比。

## Decision

**覆盖率基线的唯一来源 = CI `build-test` 作业 cobertura 的 `line-rate`**；`README` 双语徽章与 `docs/testing.md` 基线行随该值更新（复现命令、打印步骤与留存期见 `docs/testing.md`），本机实测值不作基线来源。

`ci.yml` 的路径过滤使 `build-test` 只在 code 面命中时运行：纯文档批次拿不到新值，徽章与基线行沿用最近一次 code 面作业的值。

## Alternatives considered

- **继续按本机 CI 同款命令实测**：落败——本机值随机器可写性变化（成因见 `Problem` 的【推断 · 未证】：落盘失败 `catch` 白送 4 行），偏高还会被读成收益；同一代码面上 CI 值逐作业复现，本机值只在固定环境下才稳定。
- **徽章取整（如 `55%`）**：落败——抹平差异不解决来源问题；且徽章与 `docs/testing.md` 的精确基线行会变成同一事实的两个口径。
- **徽章接动态服务（Gist / shields endpoint / Coveralls）**：落败——为一行静态徽章引入外部状态、凭据与可用性面，超出文档批次。
- **把本机 4 行环境差从 cobertura 里过滤后再发布**：落败——CI 值本就无此差，为此新增过滤脚本与失败面不划算。

## Consequences

- 徽章、`docs/testing.md` 基线行与 CI 打印值三者同源，可用 `gh run view <id> --log | grep line-rate` 或下载 artifact 复核（`HOME` 只读的环境下 gh 写 `~/.cache/gh` 会失败，需 `XDG_CACHE_HOME=<可写目录>`）。
- 本机自测的覆盖率不再要求与徽章一致；口径写在 `docs/testing.md`。
- 跟值义务的**人工面** = 收尾检查单「README 双语同步」条：其对象是 README 徽章（要求核对测试徽章与实测基线/覆盖率），`docs/testing.md` 基线行靠同一动作顺手同步。**机械面** = [README 徽章与基线行一致性门禁](2026-09-13-readme-badge-baseline-gate.md)（`verify-readme-badges.py`）：只断言两个家相等，不校验值是否等于 CI 实测——`verify-*.py` 不跑 `dotnet test`，跟值仍是人工作业。本批即由收尾条发现徽章停在旧值。
- 同批修正 `README` 双语、`docs/faq{,.en}.md` 与 `docs/user-guide{,.en}.md` 对端口稳定性的无条件断言：首选端口被非血统进程占用时本次启动漂移，origin 变、页面级会话记忆不保留；漂移发生在页面已加载之后时才追加「该会话设置页命令面失效，重启应用恢复」。事实源为 [port-drift-ipc-origin-mismatch](../bug-fix/2026-09-12-port-drift-ipc-origin-mismatch.md)。
- 本批零 `src/tests` 变更；`verify-md-links.py`、`verify-doc-budgets.py`、`verify-adr-format.py`、`verify-handoff-structure.py` 全绿。

## Related

- [端口漂移与 IPC origin 错配](../bug-fix/2026-09-12-port-drift-ipc-origin-mismatch.md)：同批用户文档「端口保持稳定」例外句的行为事实源。
- [运行时交接收养](../bug-fix/2026-09-12-runtime-handoff-adoption.md)：其 Testing 段的覆盖率事实行随本篇口径改写（只改事实，决定未动）。
- [测试三包升级与覆盖率收集](../process/2026-09-03-test-sdk-coverage-runner-bump.md) 与 [覆盖率向纯逻辑提取要](../simplification/2026-09-03-coverage-via-pure-logic-extraction.md)：两篇的本机口径行已加指针指来本篇；口径被本篇超车，各自的决定独立未动。
- [步超时测试的机器态依赖](../bug-fix/2026-08-31-step-timeout-test-machine-state-dependence.md)：机器态依赖的相邻先例（测试结果面，本篇是覆盖率面）。
