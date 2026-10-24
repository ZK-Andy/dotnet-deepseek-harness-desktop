# Testing

> 测试基线（tests/coverage）的机器家 = [`scripts/test-baseline.json`](../scripts/test-baseline.json)；`dotnet test` 全绿 0 警告是每次提交的硬门。

## 单测

* 框架：`xunit 2.9.3`，`dotnet test dotnet-deepseek-harness-desktop.slnx`；测试工程 `tests/DeepSeek.Harness.Desktop{.Core,.Infrastructure,}.Tests/`；**平台条件用例用 `UnixFact`/`UnixTheory`/`LinuxFact`（[UnixFactAttribute.cs](../tests/DeepSeek.Harness.Desktop.Infrastructure.Tests/UnixFactAttribute.cs)）并写原因；新用例禁 `if (!IsLinux) return;` 早退，dsh 启动断言走 `DshSpawnAssert`**。
* **覆盖率基线取 CI**：`scripts/test-baseline.json` 的 `coverage` 值与 README 同值徽章取自最近一次真正跑了 `build-test` 的作业 cobertura 的 `line-rate`（`ci.yml` 按路径过滤，只在 code 面命中时跑该作业——纯文档批次沿用上一次的值）；`coverage summary` 步把每个测试工程各产出的 cobertura 按 `(assembly, 源路径, 行号)` 取并集后打印 `coverage-summary: covered=<c> valid=<v> line-rate=<rate>`（合并规则与脚本自测见 [`scripts/coverage-summary.py`](../scripts/coverage-summary.py) 与 [ADR](../.agents/notes/implemented/testing/2026-09-14-coverage-baseline-multi-project-merge.md)）、`coverage-cobertura` artifact 存 7 天；复现为 `gh run download <id> -n coverage-cobertura` 后 `python3 scripts/coverage-summary.py --results <dir>`，或 `gh run view <id> --log | grep coverage-summary`。本机复现用 `-c Debug --collect:"XPlat Code Coverage"`：本机与基线可差数行（`HostLog.cs` 落盘 `catch` 的覆盖取决于 `HOME` 可写，定性【推断 · 未证】）；`-c Release` 的序列点集合与 Debug 不同口径，不与基线可比。见 [ADR](../.agents/notes/implemented/testing/2026-09-12-coverage-baseline-from-ci-cobertura.md)。
* **覆盖率是带容差的门**：`coverage summary` 步带 `--baseline scripts/test-baseline.json --tolerance-pp 0.5`——实测 `line-rate` 低于基线超过 0.5 个百分点即 exit 3 判红，并打出 `baseline`/`floor`/`measured` 三值便于判读；**比对只在 `test with coverage` 步成功时进行**，测试红了只复算不判门（半量 TestResults 会把「测试失败」误诊成「覆盖率下滑」）。容差不是松弛而是判据精度：逐轮覆盖率有微小漂移（同上「可差数行」），精确比对会把 CI 变成随机红；**真实下滑超过容差时，要么补测试，要么按「跟值」流程在同一变更里更新 `scripts/test-baseline.json` 与 README 双语徽章**（三者同值由 `verify-readme-badges.py` 强制）。基线文件缺失或形状不符即 exit 4 fail loud，绝不读成「无下限、通过」。
* 覆盖面（按域分组，逐类细节见各文件头 `<summary>`）：
  * **壳与运行时**：`HarnessUrlParserTests`（`dsh web:` 行解析）、`HarnessRuntimeHostTests`（端口分配/记忆/占位回退/生命周期门/取消契约）、`RuntimeVersionGateTests`（版本底线判定 + 底线横幅）、`DesktopProfileBootstrapTests`/`SharedHomeContractTests`（desktop profile 自举与共享 home 契约）、`RunMarkerTests`（非受控退出标记与横幅）、`PathLinkGuardTests`（profile/锁路径拒符号链接判定）、`DesktopBannerTests`（横幅工厂幂等/堆叠/转义）。
  * **监督与观测**：`PageHealthMonitorTests`（页面健康探针）、`HostLogAndDiagnosticsTests`（HostLog 出口脱敏集成）、`SecretMaskerTests`（凭据形状遮罩纯函数）、`DiagnosticsTests`/`RecoveryPageTests`/`RecoverySurfaceTests`/`StartupFailureFaceTests`（诊断、恢复页与失败面）。
  * **托盘/窗口/单实例**：`ShellTrayActionTests`/`CloseToTrayTests`（托盘菜单映射与关到托盘偏好）、`LauncherActivationTests`（单实例仲裁/uid 回退后缀）、`CaptionBarTests`/`WindowCommandRouterTests`（顶栏脚本与窗口命令）。
  * **companion 客户端 JS 腿**：在 `plugins/dsh-desktop-companion/` 跑 `node --test`（Node 22，无第三方依赖；`testkit/harness.js` 提供 DOM 桩、可控时钟、Cordis 上下文、React 桩，按真实装载路径驱动 `client.js`，见 [ADR](../.agents/notes/implemented/testing/2026-10-21-companion-client-js-tests.md)）。覆盖设置页三块 UI、更新按钮、locale 桥、外链 toast 与桥注入延迟容错；`test/`、`testkit/` 不进 tgz。
  * **随包插件**：`MarketInstallHelperTests`（检测/迁移/workspace 修正/伴生 spec/JsonNode 写盘格式钉子）、`PluginVersionCheckTests`（版本解包/升级判定/脏版本 fail loud）、`BundledPluginCatalogTests`（清单装配判定/端到端布局）、`DesktopProfileBootstrapTests`（reconcile 不可解析引用）。
  * **自更新**：`UpdateVersionTests`、`UpdateStateMachineTests`（对账/恢复/并发去重）、`ReleaseAssetTests`（资产挑选/SUMS 解析/下载锁）、`FileReadyPersistenceTests`（ready.json 往返契约）、`InstallerDownloaderTests`（SHA256SUMS 强校验/原子改名）、`UpdateInstallerTests`（deb/rpm 命令 + Linux root 脚本内容级回归：PATH 硬化/哈希复验/symlink 守卫/降权拉起）、`DesktopUpdateCommandRouterTests`（后台 token 契约）、`UpdateOptionsTests`/`UpdateStateJsonTests`/`AppJsonTests`、`UpdateHttpClientExposureTests`（自更新链路静态客户端来源唯一）。
  * **导航与外链**：`ExternalLinkPolicyTests`、`ExternalLinkCommandRouterTests`、`ShellBridgeTests`/`ShellBridgeScriptTests`（信封/nonce/origin 判据/分发）、`AvaloniaWebViewNavigatorTests`（导航到达与探针解码）。
  * **退出与环境**：`ExitPipelineTests`（退出管道记序与幂等契约）、`DevEnvironmentTests`（dev 隔离）、`UpdateBannerTests`（更新就绪横幅）、`AutostartBuilderTests`（自启条目构造）。
  * **架构边界**：`ArchitectureTests`（依赖方向断言）；Core 纯度门（Core 源文件不得直接做文件/环境 IO，IO 经端口、
    适配器落 Infrastructure）是独立门禁 `verify-core-purity.py`（见「门禁」表）。
* 运行：沙箱需 `DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget`（`/home` 只读）。

```sh
export DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget
dotnet test dotnet-deepseek-harness-desktop.slnx -c Release   # 本地跑测；复现覆盖率基线须用 -c Debug（见「单测」）
```

## 门禁

| 脚本 | 作用 | 命令 |
|---|---|---|
| `verify-adr-format.py` | `ADR` 头/骨架/状态-目录一致 + 命名校验 | `python3 scripts/verify-adr-format.py` |
| `verify-cookbook.py` | 踩坑记录格式/阶段标签封闭集（缺省校验主档 + 冷归档层） | `python3 scripts/verify-cookbook.py` |
| `verify-doc-budgets.py` | durable 文档字数预算（上限表见根 `AGENTS.md`，清单在 manifest） | `python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json` |
| `verify-md-links.py` | 相对链接/锚点/目标须入库 | `python3 scripts/verify-md-links.py` |
| `verify-readme-badges.py` | README 双语 `tests`/`coverage` 徽章 == `scripts/test-baseline.json` | `python3 scripts/verify-readme-badges.py` |
| `verify-handoff-structure.py` | HANDOFF 滚动窗/状态区 | `python3 scripts/verify-handoff-structure.py` |
| `verify-governance.py` | Issue/PR 模板治理字段 + 工作流/composite action 的 run: 禁插值事件载荷/env 回读 | `python3 scripts/verify-governance.py` |
| `verify-skill-format.py` | 技能格式（frontmatter/目录束/内链/结构，`.agents/skills/*/SKILL.md`） | `python3 scripts/verify-skill-format.py` |
| `verify-shell-standards.sh` | 脚本规范 S1-S6（errexit / 尺寸 ≤250 行 / 库不可执行且不自设选项 / 旋钮声明 / 共享库单源 / `shellcheck -S warning`），规范见 [script-standards](script-standards.md) | `bash scripts/verify-shell-standards.sh`（`--self-test` 夹具） |
| `verify-host-command-ids.py` | 命令 ID 单一事实源（壳侧不复写已知 ID 字面量 + 插件侧不自造命令 + 常量不悬空；`--enforce` 才 fail） | `python3 scripts/verify-host-command-ids.py`（`--self-test` 夹具） |
| `verify-core-purity.py` | Core 纯度闸（Core 源文件不得直接做文件/环境 IO：IO 经 Core 端口、适配器落 Infrastructure；扫描用 `gate_common` 的 `CSharpLineScanner`，注释与字符串内的同形词不判红） | `python3 scripts/verify-core-purity.py`（`--self-test` 夹具） |
| `install-linters.sh` + `actionlint` | 脚本层静态检查工具（shellcheck + actionlint，版本与 sha256 双钉）与工作流静态检查（对 `run:` 块跑 `shellcheck -S warning`） | `bash scripts/install-linters.sh` → `actionlint -shellcheck="shellcheck -S warning"` |
| `verify-compose-root.py` | 装配面语义闸（根只装配：方法清单封闭 / new 自有类型 ≤6 / 单 dot 分部 / 禁同步编排）+ 排序器形状（C6；`--enforce` 才 fail） | `python3 scripts/verify-compose-root.py` |
| `verify-review-brief.py` | 评审简报形状 + 评审对象冻结 + 轮次纪律（轮次上限、标题↔文件名互校、处置段与条目、验轮 `base` 须为上一轮冻结 tree 且增量非空）（`--enforce`；简报是 gitignore 本地文档） | `python3 scripts/verify-review-brief.py --enforce` |
| `change-scope.sh` | `push` 前最小证据（`merge-base` diff） | `scripts/change-scope.sh` |

每道判据只有一份实现，可以在多档执行（`hooks` 只做快检查，`CI` 拥有穷尽矩阵）：

| 门 | `ci.yml` docs job | `pre-commit` | `pre-push` |
|---|---|---|---|
| `verify-adr-format.py` | ✓ | ✓（`.agents/notes/**` 有暂存变更时） | ✓ |
| `verify-cookbook.py` | ✓ | ✓ | — |
| `verify-doc-budgets.py` | ✓ | ✓ | ✓ |
| `verify-md-links.py` | ✓ | ✓ | ✓ |
| `verify-readme-badges.py` | ✓ | ✓（`--staged`） | — |
| `verify-handoff-structure.py` | ✓ | ✓（本地文档存在即校验） | — |
| `verify-governance.py` | ✓（`governance.yml` 的「治理门禁」步同源调它） | ✓ | — |
| `verify-skill-format.py` | ✓ | — | — |
| `verify-shell-standards.sh`（含 shellcheck） | ✓（先跑 `install-linters.sh`） | ✓ | — |
| `install-linters.sh` + `actionlint` | ✓ | —（本地按需自跑） | — |
| `verify-review-brief.py` | —（简报不入库，CI 看不见） | ✓（`.review-briefs/` 有在审简报时） | — |
| `verify-code-health.py` / `verify-code-conventions.py` | ✓（`--enforce`） | ✓ | — |
| `verify-review-tier.py` | ✓（`--since`） | ✓（`--staged`） | ✓（`--since origin/main`） |
| `verify-ui-copy.py` | ✓（`--self-test` + 全量） | — | — |
| `verify-host-command-ids.py` | ✓（`--self-test` + 全量 `--enforce`） | ✓ | — |
| `verify-core-purity.py` | ✓（`--self-test` + 全量 `--enforce`） | ✓ | — |
| `package-version.sh` | ✓（`--self-test`；判据本体在打包腿调用链上） | — | — |
| `release-preflight.sh` | ✓（`--self-test`；判据本体在 tag 触发的发布腿调用链上） | — | — |
| `release-notes.sh` | ✓（`--self-test`；判据本体在 tag 触发的发布腿调用链上） | — | — |
| `coverage-summary.py` | ✓（`--self-test`；判据本体在 `build-test` 的 coverage summary 步） | — | — |
| `dev-sign.sh` | ✓（`--self-test`：桩 `codesign`/`signtool`/`pwsh` 记录调用面；判据本体在 mac/win 打包腿的 `SELF_SIGN=1` 路径） | — | — |
| `smoke-linux-rpm.sh` | ✓（`--self-test`：桩 `docker` 记录挂载/env/镜像；判据本体在 Linux 打包腿的 rpm 容器冒烟步） | — | — |
| `change-scope.sh` | — | — | ✓（取证型，非判据：`\|\| true` 只打印范围） |

`CI` (`ci.yml`)：`docs` job 无条件跑上表全部门禁，另跑两道**工具门**（`actionlint` 工作流静态检查 + `shellcheck -S warning`，工具由 `install-linters.sh` 钉版装）与**离线夹具自测**（三平台冒烟共用面、包布局三平台分支、`dev-sign` 自签调用面、rpm 容器腿参数面、三包脚本共用头部与 Inno 模板渲染、发布总检、发布正文、覆盖率并集复算——此前只在打包腿上执行）；`companion-js` job 无条件跑客户端 JS 测试（`node --test`，零依赖）；`build-test` job 三平台矩阵（ubuntu/windows/macos，`fail-fast: false`；ADR cross-process-filelock-semantics），只在 code 面命中时跑——code 面含 `src/**`、`tests/**`、`scripts/**`、`tools/**`、`assets/icon.png`、`assets/icon.ico`、`*.slnx`、`.editorconfig`、`Directory.*.props`、`global.json` 与 `ci.yml` 自身（结构级构建输入与参与构建的图标都能翻转结论，漏判即整条门禁跳过；`assets/` 只列构建图标，README 图与 hicolor 档是文档面/打包腿输入；`packaging/` 模板由无条件 docs job 的 `package-windows.sh --self-test` 离线渲染覆盖）。format 门禁与 `test with coverage`（`coverage summary` 步合并后**比对基线**，`upload-artifact 7d`）仅 ubuntu 腿；三腿都跑**全量**套件（四个测试工程在真内核上执行）。

## 冒烟与集成

* **沙箱冒烟**：`HarnessRuntimeHost` `StartAsync` 抓 `dsh web:`（`60s`），`RuntimeSupervisor` `kill` 子进程→自动重启+换 `URL`（回归断言重启 `URL` 相同以保 `origin`）。
* **本机冒烟**：`dotnet run --project src/DeepSeek.Harness.Desktop` 起宿主窗口加载 `dsh web:`（需 `DEEPSEEK_API_KEY` 与 `GTK3` + `WebKitGTK 4.1`）。`DSH_DEVTOOLS=1` 开 `WebView` 调试。
* **打包自检**：`verify-package-layout.sh --platform <linux|macos|windows>`（必填）断言内容根无闭包残留、插件 tgz 过名称/体积关、主程序在位且带可执行位；原生库断言 `libSkiaSharp`+`libHarfBuzzSharp`（macOS 加 `libAvaloniaNative`，Windows 加 `av_libglesv2`）在根或 `runtimes/<rid>/native/` 命中且后者存在；macOS 另断言 `Info.plist` 声明 `CFBundleIconFile` 且 `Resources/icon.icns` 在位；Windows 追加托管程序集与 `wwwroot`；`release-preflight.sh` 复核资产矩阵/体积下限（15MB）/SHA256SUMS（离线自测）；`smoke-install-{linux,windows,macos}.sh` 三平台「静默安装/拷装 → 启动 → 双信号」（Linux 全链/安装链、win/mac runner 有桌面会话应达全链）。
* **打包与发布流水线**：三平台打包的唯一家是 [`.github/workflows/package.yml`](../.github/workflows/package.yml)（`workflow_call` + `workflow_dispatch`，三平台 job 共用 [`.github/actions/package-setup`](../.github/actions/package-setup/action.yml) 前置；版本解析与 tag/输入一致性判据的唯一实现在 [`scripts/package-version.sh`](../scripts/package-version.sh)）。tag 发布走 [`release.yml`](../.github/workflows/release.yml)：先把三个包腿作为 reusable workflow 调用，再以 `needs:` 在**同一 run** 内 `download-artifact` 聚合 → preflight → 创建单个 Release（包腿失败即发布作业不启动）。
* **插件面**：装配机制与 spawn 前就位的单一事实源在 [architecture.md](architecture.md)「插件装配」；测试关注点：`MarketInstallHelperTests`/`BundledPluginCatalogTests`/`DesktopProfileBootstrapTests`（见「单测」覆盖面）。

## 行为级回归要求

* 变更 `HarnessRuntimeHost`（含退出/生命周期）、dsh 版本探测入口（`RuntimeVersionGate.ProbeAsync`）、`dsh web:` 解析、监督顺序、自更新安全防线（pkexec 脚本/哈希复验）、打包布局必须配套回归/快照；`mock` 仅用于昂贵/非确定性边界。
