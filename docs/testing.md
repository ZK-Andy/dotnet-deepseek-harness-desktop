# Testing

> `12/12` 通过，`v0.1.12` 基线。

## 单测

* 框架：`xunit 2.9.3`（`v3` 断言差异见仓库记忆 `xunit_v3_api`），`dotnet test dotnet-deepseek-harness-desktop.slnx`。
* 覆盖：
  * `HarnessUrlParserTests`：`dsh web:` 行解析、端口/异常边界。
  * `HarnessRuntimeHostTests`：`port 0` 分配、记忆端口复用、占位回退、`RestartAsync`、`WaitForExitAsync`、`StderrTail`。
  * `RuntimeLocatorTests`：`DSH_DESKTOP_RUNTIME_DIR` 覆盖、`TryLocateBundled` 判 `node + dsh/lib/bin.js`。
  * `RuntimeSupervisorTests`：退出→恢复屏→重启→导航顺序，取消与重试。
  * `GreetingServiceTests`：`IPC` 命令样例。
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

`CI` (`ci.yml`) 在 `ubuntu-latest` 跑三门禁 + `dotnet build/test`；`hooks` 只做快检查，`CI` 拥有穷尽矩阵。

## 冒烟与集成

* **沙箱冒烟**：`HarnessRuntimeHost` `StartAsync` 抓 `dsh web:`（`60s`），`RuntimeSupervisor` `kill` 子进程→自动重启+换 `URL`（回归断言重启 `URL` 相同以保 `origin`）。
* **本机冒烟**：`dotnet run --project src/DeepSeek.Harness.Desktop` 起 `Ryn` 窗口加载 `dsh web:`（需 `DEEPSEEK_API_KEY` 与 `WebKitGTK`）。`DSH_DEVTOOLS=1` 开 `WebView` 调试。
* **打包自检**：`bundle-runtime-ci.sh` 的 `60s` 常驻抓 `dsh web:`；`package-linux.sh --stage-only` 校验 `node + dsh/lib/bin.js + dshmarket.tgz 497K`。
* **市场**：全新 `DSH_HOME` 首启 `3s` 后台 `dsh plugin add file:…tgz` → `exit 0` → `host.Stop()→Supervisor` 重启 → `bundles` 含 `dshmarket`，`Web UI` 出现市场（`0.1.12` 即时重启，`0.1.11` 需二次重启）。

## 行为级回归要求

* 变更 `HarnessRuntimeHost/Restart`、`RuntimeLocator` 入口、`dsh web:` 解析、监督顺序、打包布局必须配套回归/快照；`mock` 仅用于昂贵/非确定性边界。
