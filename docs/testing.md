# Testing

> 测试基线以 README 双语徽章为准（当前 347/347，覆盖率 49%+）；`dotnet test` 全绿 0 警告是每次提交的硬门。

## 单测

* 框架：`xunit 2.9.3`，`dotnet test dotnet-deepseek-harness-desktop.slnx`；测试工程 `tests/DeepSeek.Harness.Desktop.Tests/`（35 个测试文件）。
* 覆盖面（按域分组，逐类细节见各文件头 `<summary>`）：
  * **壳与运行时**：`HarnessUrlParserTests`（`dsh web:` 行解析）、`HarnessRuntimeHostTests`（端口分配/记忆/占位回退/生命周期门/取消契约）、`RuntimeLocatorTests`（捆绑优先/PATH 回退）、`RuntimeVersionGateTests`（版本底线判定 + 底线横幅）、`DesktopProfileBootstrapTests`/`SharedHomeContractTests`（desktop profile 自举与共享 home 契约）、`RunMarkerTests`（非受控退出标记与横幅）、`DesktopBannerTests`（横幅工厂幂等/堆叠/转义）。
  * **监督与观测**：`PageHealthMonitorTests`（页面健康探针）、`HostLogAndDiagnosticsTests`（HostLog 出口脱敏集成）、`SecretMaskerTests`（凭据形状遮罩纯函数）、`DiagnosticsTests`（诊断包导出）、`RecoveryPageTests`（恢复页脚本构建）。
  * **托盘/窗口/单实例**：`TrayTests`（托盘命令记序）、`CloseToTrayTests`（关到托盘偏好）、`LauncherActivationTests`（单实例仲裁/uid 回退后缀）、`DesktopBannerTests`。
  * **随包插件**：`MarketInstallHelperTests`（检测/迁移/workspace 修正/伴生 spec/JsonNode 写盘格式钉子）、`PluginVersionCheckTests`（版本解包/升级判定/脏版本 fail loud）、`BundledPluginCatalogTests`（清单装配判定/端到端布局）、`DesktopProfileBootstrapTests`（reconcile 不可解析引用）。
  * **自更新**：`UpdateVersionTests`、`UpdateStateMachineTests`（对账/恢复/并发去重）、`ReleaseAssetTests`（资产挑选/SUMS 解析/下载锁）、`InstallerDownloaderTests`（SHA256SUMS 强校验/原子改名）、`UpdateInstallerTests`（deb/rpm 命令 + Linux root 脚本内容级回归：PATH 硬化/哈希复验/symlink 守卫/降权拉起）、`DesktopUpdateCommandRouterTests`（后台 token 契约）、`UpdateOptionsTests`/`UpdateStateJsonTests`/`AppJsonTests`。
  * **导航与外链**：`ExternalLinkPolicyTests`、`ExternalLinkCommandRouterTests`、`RynNavigationCallbacksTests`（拦截/放行/origin 刷新/失败 toast）。
  * **退出与环境**：`ExitOrchestrationTests`（退出编排记序契约）、`DevEnvironmentTests`（dev 隔离）、`ConvenienceTests`（更新就绪横幅/自启条目）。
* 运行：沙箱需 `DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget`（`/home` 只读）。

```sh
export DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget
dotnet test dotnet-deepseek-harness-desktop.slnx -c Release
```

## 门禁

| 脚本 | 作用 | 命令 |
|---|---|---|
| `verify-adr-format.py` | `ADR` 头/骨架/状态-目录一致 + 命名校验 | `python3 scripts/verify-adr-format.py` |
| `verify-cookbook.py` | 踩坑记录格式/阶段标签封闭集 | `python3 scripts/verify-cookbook.py` |
| `verify-doc-budgets.py` | 四份 durable 文档字数预算 | `python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json` |
| `verify-md-links.py` | 相对链接/锚点 | `python3 scripts/verify-md-links.py` |
| `verify-handoff-structure.py` | HANDOFF 滚动窗/状态区 | `python3 scripts/verify-handoff-structure.py` |
| `verify-governance.py` | Issue/PR 模板治理字段 | `python3 scripts/verify-governance.py` |
| `change-scope.sh` | `push` 前最小证据（`merge-base` diff） | `scripts/change-scope.sh` |

`CI` (`ci.yml`)：`docs` job 无条件跑六文档门禁；`build-test` job 只在 code 面命中时跑 `dotnet build` + `test with coverage --collect:"XPlat Code Coverage" cobertura`（`upload-artifact 7d`）。`hooks` 只做快检查，`CI` 拥有穷尽矩阵。

## 冒烟与集成

* **沙箱冒烟**：`HarnessRuntimeHost` `StartAsync` 抓 `dsh web:`（`60s`），`RuntimeSupervisor` `kill` 子进程→自动重启+换 `URL`（回归断言重启 `URL` 相同以保 `origin`）。
* **本机冒烟**：`dotnet run --project src/DeepSeek.Harness.Desktop` 起 `Ryn` 窗口加载 `dsh web:`（需 `DEEPSEEK_API_KEY` 与 `WebKitGTK`）。`DSH_DEVTOOLS=1` 开 `WebView` 调试。
* **打包自检**：`verify-package-layout.sh` 断言安装器 staging 无闭包残留、插件 tgz 过名称/体积关（`build-companion-tgz.sh` 打包时现打现校验，新鲜度由「现打直进 staging」结构性保证）；`release-preflight.sh` 发布前复核资产矩阵/体积下限（15MB）/SHA256SUMS；`smoke-install-{linux,windows,macos}.sh` 三平台「静默安装/拷装 → 启动 → 双信号」（Linux 全链/安装链、win/mac runner 有桌面会话应达全链）。
* **随包插件**：首启引导经 `RuntimeBootstrap` registry 装市场（`dshmarket@latest`）+ 后台按 `BundledPluginCatalog` 清单装 companion（自安装器 `resources/plugins`；版本感知升级）→ `host.Stop()→Supervisor` 重启 → `Web UI` 出现市场、外链点击开系统浏览器；启动前 `DesktopProfileBootstrap.ReconcileProfile` 移除不可解析 bundle 引用（见 [architecture.md](architecture.md)）。

## 行为级回归要求

* 变更 `HarnessRuntimeHost`（含退出/生命周期）、`RuntimeLocator` 入口、`dsh web:` 解析、监督顺序、自更新安全防线（pkexec 脚本/哈希复验）、打包布局必须配套回归/快照；`mock` 仅用于昂贵/非确定性边界。
