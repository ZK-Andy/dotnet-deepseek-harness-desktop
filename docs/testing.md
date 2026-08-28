# Testing

> `245/245` 通过，`dotnet test` 当前基线。

## 单测

* 框架：`xunit 2.9.3`（`v3` 断言差异见仓库记忆 `xunit_v3_api`），`dotnet test dotnet-deepseek-harness-desktop.slnx`。
* 覆盖：
  * `HarnessUrlParserTests`：`dsh web:` 行解析、端口/异常边界。
  * `HarnessRuntimeHostTests`：`port 0` 分配、记忆端口复用、占位回退、`RestartAsync`、`WaitForExitAsync`、`StderrTail`。
  * `RuntimeLocatorTests`：`DSH_DESKTOP_RUNTIME_DIR` 覆盖、`TryLocateBundled` 判 `node + dsh/lib/bin.js`。
  * `MarketInstallHelperTests`：`IsBundleInstalled(pkg)` 真/假/异常/按包独立、`CleanupBogusApp` 删 `app` 的 `tgz`/`NoOp`、`EnsureWorkspaceAllowBuilds` 占位替换与 `esbuild` 追加、`ResolveMarketSpec` 的 `tgz>10K/目录/registry` 三分支、`ResolveCompanionSpec` 的 `tgz>1K/目录/null` 三分支（无 registry 回退）、`EnsureBundlesContainsAsync` 追加/幂等/双包共存。
  * `PluginVersionCheckTests`：tgz/目录两种随包形态解 version（含 `./package/` 前缀与无关条目）、损坏包与坏 version 字段 fail loud、已装副本缺失/损坏返 null、升级判定边界、脏版本段抛错。
  * `BundledPluginCatalogTests`：清单装配判定（未装即装不读版本/落后即升带版本日志/同版与更高跳过）、副本缺失修复重装、spec 缺失与解析器异常单项隔离、registry 回退串放弃升级检查保留首装、待装顺序随清单、真实闭包布局端到端与空闭包回退。
  * `ExternalLinkPolicyTests`：站外/同源/非 http(s)/空 href/origin 边界的纯判定。
  * `ExternalLinkCommandRouterTests`：命令路由与打开器委托。
  * `RynNavigationCallbacksTests`：导航层外部链接拦截（站外 http(s) Block+打开器/同源与非 http(s) 放行/打开器抛异常仍 Block/无 origin 保守拦截）与 `WebViewNavigated`「页面已到达」回调。
  * `UpdateVersionTests`：版本逐段比较（v 前缀/缺段补 0/预发布截断/**任一段非法 fail loud**）。
  * `UpdateStateMachineTests`：启动对账清 stale ready（相等/**旧版本**/损坏版本串）、持久化恢复 ready、无更新/旧版→up-to-date、新版下载→ready、下载失败→error 后可恢复、install 仅 ready/成功转 installing/失败回 ready、订阅与退订、**并发检查只跑一次**。
  * `ReleaseAssetTests`：按 RID 挑资产（deb/exe/dmg）、SHA256SUMS 双空格与 `*` 二进制格式解析、跨实例下载锁互斥、包类型检测回退。
  * `DevEnvironmentTests`：dev 识别、ApplicationId 后缀、隔离 home 两级上溯推导与空输入。
  * `InstallerDownloaderTests`：HTTP 非 2xx fail loud、release 未附 SHA256SUMS 拒装、哈希不匹配清理 `.part`、条目缺失拒装、成功路径原子改名（`.download.lock` 常驻属设计）。
  * `UpdateInstallerTests`：deb/rpm 包命令与不支持扩展名、Linux 安装脚本内容（等待环/runuser 降权/变量透传/路径转义）。
  * `UpdateOptionsTests`：dev 门禁（非 dev 恒装载；dev 需 `DSH_DESKTOP_UPDATE_FORCE=1`）。
* 运行：沙箱需 `DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget`（`/home` 只读）。

```sh
export DOTNET_CLI_HOME=$PWD/.dotnet-cache/cli NUGET_PACKAGES=$PWD/.dotnet-cache/nuget
dotnet test dotnet-deepseek-harness-desktop.slnx -c Release
```

## 门禁

| 脚本 | 作用 | 命令 |
|---|---|---|
| `verify-adr-format.py` | `ADR` 头/骨架/状态-目录一致 | `python3 scripts/verify-adr-format.py` |
| `verify-doc-budgets.py` | `AGENTS.md` 字数预算 | `python3 scripts/verify-doc-budgets.py --manifest scripts/doc-budgets.manifest.json` |
| `verify-md-links.py` | 相对链接/锚点（排除 `skills/.dotnet-cache/bin/obj`） | `python3 scripts/verify-md-links.py` |
| `change-scope.sh` | `push` 前最小证据（`merge-base` diff） | `scripts/change-scope.sh` |

`CI` (`ci.yml`) 在 `ubuntu-latest` 跑三门禁 + `dotnet build` + `test with coverage --collect:"XPlat Code Coverage" coberture`（`upload-artifact 7d`）；`hooks` 只做快检查，`CI` 拥有穷尽矩阵。

## 冒烟与集成

* **沙箱冒烟**：`HarnessRuntimeHost` `StartAsync` 抓 `dsh web:`（`60s`），`RuntimeSupervisor` `kill` 子进程→自动重启+换 `URL`（回归断言重启 `URL` 相同以保 `origin`）。
* **本机冒烟**：`dotnet run --project src/DeepSeek.Harness.Desktop` 起 `Ryn` 窗口加载 `dsh web:`（需 `DEEPSEEK_API_KEY` 与 `WebKitGTK`）。`DSH_DEVTOOLS=1` 开 `WebView` 调试。
* **打包自检**：`bundle-runtime-ci.sh` 的 `60s` 常驻抓 `dsh web:`；`package-linux.sh --stage-only` 校验 `node + dsh/lib/bin.js + dshmarket.tgz 497K`。
* **随包插件**：全新 `DSH_HOME` 首启 `3s` 后台单条 `dsh plugin add <spec…>` 装齐 `dshmarket + dsh-desktop-companion` → `exit 0` → `host.Stop()→Supervisor` 重启 → `bundles` 含两项，`Web UI` 出现市场、外链点击开系统浏览器（伴生已通过正式版实机验收：外链单标签、站内无影响）。

## 行为级回归要求

* 变更 `HarnessRuntimeHost/Restart`、`RuntimeLocator` 入口、`dsh web:` 解析、监督顺序、打包布局必须配套回归/快照；`mock` 仅用于昂贵/非确定性边界。
